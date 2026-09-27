using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using OsmoOverlay.Core.Reframe;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     The preview's decoder, in-process through the FFmpeg shared libraries (FFmpeg.AutoGen, loaded by LibavLoader).
///     Each segment's file is opened once and kept open, so a seek is a demuxer seek plus decoding from the keyframe -
///     no process spawn and no re-reading of the file's index (an ffmpeg process per seek takes 1.3-1.7 s on a 4K
///     Osmo file).
///     Decodes on the GPU where there is a decoder for it (LibavStreamDecoder.Open), else multi-threaded in software.
///     The decoder remembers where it is: the next frame (stepping, playback) just decodes on, a frame shortly
///     ahead decodes forward without seeking, and stepping backwards one frame at a time is served from the frames
///     leading up to it, decoded in the same pass (BackStepFrames). One lock serializes all decoding - the
///     preview only ever needs one frame at a time.
/// </summary>
public sealed unsafe class LibavVideoSource : IDisposable
{
	private const int MaxOpenSessions = 2;
	// Decoding on at ~150+ fps beats a seek plus the keyframe run-up for a target up to this far ahead.
	private const double DecodeAheadSeconds = 1.0;
	// A keyframe request only decodes on this far - past it a keyframe seek (~15 ms) is cheaper, and one keyframe per
	// second (the timeline's thumbnails, a drag) would otherwise decode a whole second of frames each time.
	private const int KeyframeDecodeAheadFrames = 6;
	// Frames before a one-frame back step that the same decode pass keeps, so the next steps back need no decoding -
	// at most this many, and at most BackStepCacheBytes of them (a full-resolution 4K preview frame is 33 MB).
	private const int BackStepFrames = 15;
	private const long BackStepCacheBytes = 160L * 1024 * 1024;

	private readonly Lock _lock = new();
	private readonly FrameBufferPool _buffers;
	private readonly int _backStepFrames;
	private readonly List<Segment> _segments;
	private readonly List<DecoderSession> _sessions = [];
	private readonly Dictionary<long, byte[]> _backStepCache = [];
	private readonly int _width;
	private readonly int _height;
	private readonly long _totalFrames;
	// A 360 recording's flat picture - replaced by SetView, read under _lock.
	private Reframer? _reframer;
	private DecoderSession? _positioned;
	// The global frame the positioned session outputs next when decoding on.
	private long _nextFrame = -1;
	private long _lastReturned = -1;
	private bool _disposed;

	/// <param name="reframer">A 360 recording's lenses and view - its frames are then that flat view of them (FisheyeProjector).</param>
	public LibavVideoSource(IReadOnlyList<PlaybackSegment> segments, double fps, int width, int height, Reframer? reframer = null)
		: this(segments, fps, width, height, null, reframer)
	{
	}

	/// <param name="buffers">
	///     Where frame buffers come from and go back to (Recycle) - the preview shares one with everything its frames
	///     pass through; null for a pool of this source's own.
	/// </param>
	internal LibavVideoSource(IReadOnlyList<PlaybackSegment> segments, double fps, int width, int height, FrameBufferPool? buffers,
		Reframer? reframer)
	{
		if (LibavLoader.TryLoad() is { } failure) throw new InvalidOperationException($"The preview can't decode video: {failure}");

		_reframer = reframer;
		Fps = fps;
		_width = width;
		_height = height;
		_backStepFrames = (int)Math.Clamp(BackStepCacheBytes / ((long)width * height * 4), 2, BackStepFrames);
		_buffers = buffers ?? new FrameBufferPool(_backStepFrames + 6);

		_segments = new List<Segment>(segments.Count);
		double offset = 0;
		foreach (PlaybackSegment segment in segments)
		{
			long start = (long)Math.Round(offset * fps);
			offset += segment.DurationSeconds;
			_segments.Add(new Segment(segment.Path, start, (long)Math.Round(offset * fps) - start));
		}

		_totalFrames = _segments[^1].StartFrame + _segments[^1].FrameCount;
		Duration = TimeSpan.FromSeconds(offset);

		// Opened up front: a broken file or a decoder that won't start should fail here, where the caller can
		// still fall back, not on the first seek.
		lock (_lock) SessionFor(0);
	}

	public TimeSpan Duration { get; }
	public double Fps { get; }

	/// <summary>"d3d11va" or "software" - for the log line saying how the preview decodes.</summary>
	public string DecoderDescription
	{
		get
		{
			lock (_lock) return _sessions.Count > 0 ? _sessions[0].Hardware ?? "software" : "not opened";
		}
	}

	/// <summary>The frame shown at a position (PreviewFrames.IndexAt), or null if <paramref name="ct" /> was cancelled first - a superseded seek is expected, not exceptional.</summary>
	public VideoFrame? GetFrame(TimeSpan position, SeekAccuracy accuracy, CancellationToken ct)
	{
		long target = Math.Clamp(PreviewFrames.IndexAt(position.TotalSeconds, Fps), 0, _totalFrames - 1);
		lock (_lock)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			return DecodeAt(target, accuracy, ct)?.Frame;
		}
	}

	/// <summary>Continuous decode from a position through to the end of the recording.</summary>
	public PlaybackStream OpenPlaybackStream(TimeSpan from, CancellationToken ct)
	{
		return new PlaybackStream(this, Math.Clamp(PreviewFrames.IndexAt(from.TotalSeconds, Fps), 0, _totalFrames - 1), ct);
	}

	/// <summary>
	///     A 360 recording's view, from the next frame on - frames kept for stepping back show the old one, so they go.
	///     The frame on screen is shown again by just converting it again (DecodeAt), nothing decoded.
	/// </summary>
	public void SetView(ReframeView view)
	{
		lock (_lock)
		{
			if (_disposed || _reframer is null || view == _reframer.View) return;

			_reframer = _reframer with { View = view };
			foreach (byte[] buffer in _backStepCache.Values) _buffers.Return(buffer);
			_backStepCache.Clear();
		}
	}

	/// <summary>Hands a frame's buffer back for reuse - the caller must not touch the frame afterwards.</summary>
	public void Recycle(VideoFrame frame)
	{
		_buffers.Return(frame.Bgra);
	}

	public void Dispose()
	{
		lock (_lock)
		{
			if (_disposed) return;

			_disposed = true;
			foreach (DecoderSession session in _sessions) session.Dispose();
			_sessions.Clear();
			_backStepCache.Clear();
			_positioned = null;
		}
	}

	/// <summary>Must be called under _lock. Null when cancelled.</summary>
	private (VideoFrame Frame, long Index)? DecodeAt(long target, SeekAccuracy accuracy, CancellationToken ct)
	{
		if (_backStepCache.TryGetValue(target, out byte[]? cached)) return (Returned(Copy(cached), target), target);

		(int segmentIndex, long localTarget) = Locate(target);
		Segment segment = _segments[segmentIndex];
		DecoderSession session = SessionFor(segmentIndex);

		// The frame the decoder is on, asked for again (a 360 view changed): converted again, not decoded.
		if (ReferenceEquals(session, _positioned) && target == _nextFrame - 1 && session.HasFrame) return (Returned(Convert(session), target), target);

		double aheadLimit = accuracy == SeekAccuracy.Keyframe ? KeyframeDecodeAheadFrames : DecodeAheadSeconds * Fps;
		bool decodeOn = ReferenceEquals(session, _positioned) && target >= _nextFrame && target - _nextFrame <= aheadLimit;
		if (!decodeOn)
		{
			bool backStep = target == _lastReturned - 1;
			foreach (byte[] buffer in _backStepCache.Values) _buffers.Return(buffer);
			_backStepCache.Clear();

			session.Seek(localTarget);
			_positioned = session;
			_nextFrame = -1;

			if (accuracy == SeekAccuracy.Keyframe)
			{
				if (!session.Receive()) throw new InvalidOperationException($"No frame after seeking to frame {target} of {segment.Path}.");

				long keyframe = segment.StartFrame + session.FrameIndex();
				_nextFrame = keyframe + 1;
				return (Returned(Convert(session), keyframe), keyframe);
			}

			if (backStep) return DecodeTo(session, segment, target, target - _backStepFrames, ct);
		}

		return DecodeTo(session, segment, target, long.MaxValue, ct);
	}

	/// <summary>Decodes on to the target (or the last frame, if the file ends first), keeping the frames from cacheFrom on for stepping back.</summary>
	private (VideoFrame Frame, long Index)? DecodeTo(DecoderSession session, Segment segment, long target, long cacheFrom, CancellationToken ct)
	{
		while (true)
		{
			if (ct.IsCancellationRequested) return null;

			if (!session.Receive())
			{
				if (!session.HasFrame) throw new InvalidOperationException($"No frame decoded from {segment.Path} before its end.");

				// Durations are rounded to frames; the file's own last frame is the closest there is.
				long last = _nextFrame - 1;
				return (Returned(Convert(session), last), last);
			}

			long index = segment.StartFrame + session.FrameIndex();
			_nextFrame = index + 1;
			if (index < target)
			{
				if (index >= cacheFrom) _backStepCache[index] = Convert(session).Bgra;
				continue;
			}

			VideoFrame frame = Convert(session);
			// Kept too, so stepping forward again after stepping back stays in the cache.
			if (cacheFrom != long.MaxValue) _backStepCache[index] = Copy(frame.Bgra).Bgra;
			return (Returned(frame, index), index);
		}
	}

	/// <summary>The next frame after the one decoded last, crossing into the next segment at a segment's end. Null at the end of the recording.</summary>
	private (VideoFrame Frame, long Index)? DecodeNext(long expected, CancellationToken ct)
	{
		if (_positioned is null || _nextFrame != expected) return DecodeAt(expected, SeekAccuracy.Exact, ct);

		DecoderSession session = _positioned;
		Segment segment = _segments[session.SegmentIndex];
		if (session.Receive())
		{
			long index = segment.StartFrame + session.FrameIndex();
			_nextFrame = index + 1;
			return (Returned(Convert(session), index), index);
		}

		if (session.SegmentIndex == _segments.Count - 1) return null;

		// The next file starts where this one really ended, whatever the rounded durations said.
		Segment next = _segments[session.SegmentIndex + 1];
		DecoderSession nextSession = SessionFor(session.SegmentIndex + 1);
		nextSession.Seek(0);
		_positioned = nextSession;
		_nextFrame = next.StartFrame;
		return DecodeTo(nextSession, next, next.StartFrame, long.MaxValue, ct);
	}

	/// <summary>Decodes past the next frame without converting it - fast playback shows only every Nth. False at the end of the recording.</summary>
	private bool SkipNext(long expected, CancellationToken ct)
	{
		if (_positioned is { } session && _nextFrame == expected && session.Receive())
		{
			_nextFrame = _segments[session.SegmentIndex].StartFrame + session.FrameIndex() + 1;
			return true;
		}

		// Not positioned there, or at a file's end: DecodeNext seeks or crosses into the next file.
		if (DecodeNext(expected, ct) is not { } decoded) return false;

		Recycle(decoded.Frame);
		return true;
	}

	private VideoFrame Returned(VideoFrame frame, long index)
	{
		_lastReturned = index;
		return frame;
	}

	private VideoFrame Convert(DecoderSession session)
	{
		byte[] buffer = _buffers.Rent(_width * _height * 4);
		double seconds = (_segments[session.SegmentIndex].StartFrame + session.FrameIndex()) / Fps;
		session.ConvertInto(buffer, _width, _height, _reframer, seconds);
		return new VideoFrame(buffer, _width, _height);
	}

	private VideoFrame Copy(byte[] source)
	{
		byte[] buffer = _buffers.Rent(source.Length);
		Buffer.BlockCopy(source, 0, buffer, 0, source.Length);
		return new VideoFrame(buffer, _width, _height);
	}

	private (int Segment, long LocalFrame) Locate(long frame)
	{
		for (int i = _segments.Count - 1; i > 0; i--)
		{
			if (frame >= _segments[i].StartFrame)
				return (i, frame - _segments[i].StartFrame);
		}

		return (0, frame);
	}

	/// <summary>Opens a segment's file on first use, keeping the most recently used ones open.</summary>
	private DecoderSession SessionFor(int segmentIndex)
	{
		DecoderSession? session = _sessions.Find(s => s.SegmentIndex == segmentIndex);
		if (session is not null)
		{
			_sessions.Remove(session);
			_sessions.Add(session);
			return session;
		}

		session = DecoderSession.Open(_segments[segmentIndex].Path, segmentIndex, Fps, _reframer?.Lenses);
		_sessions.Add(session);
		if (_sessions.Count > MaxOpenSessions)
		{
			DecoderSession oldest = _sessions[0];
			_sessions.RemoveAt(0);
			if (ReferenceEquals(oldest, _positioned)) _positioned = null;
			oldest.Dispose();
		}

		return session;
	}

	private sealed record Segment(string Path, long StartFrame, long FrameCount);

	/// <summary>
	///     Playback on the source's own decoder: while nothing else moves it, every frame just decodes on (and a
	///     Play right after stepping to a frame starts without any seek). If something did move it in between, the
	///     next read seeks back to where this stream is.
	/// </summary>
	public sealed class PlaybackStream
	{
		private readonly LibavVideoSource _source;
		private readonly CancellationToken _ct;
		private long _next;

		internal PlaybackStream(LibavVideoSource source, long startFrame, CancellationToken ct)
		{
			_source = source;
			_ct = ct;
			_next = startFrame;
			Position = TimeSpan.FromSeconds(startFrame / source.Fps);
		}

		/// <summary>Position of the frame TryReadNextFrame returned last, on the recording's timeline.</summary>
		public TimeSpan Position { get; private set; }

		/// <summary>Position of the frame decoded next, on the recording's timeline.</summary>
		public TimeSpan NextPosition => TimeSpan.FromSeconds(_next / _source.Fps);

		/// <summary>Why the stream stopped early, if a decode failed.</summary>
		public string? Error { get; private set; }

		/// <summary>Decodes past frames without converting them. False at the end of the recording (or once cancelled or failed).</summary>
		public bool Skip(int frames)
		{
			if (_ct.IsCancellationRequested || Error is not null) return false;

			try
			{
				lock (_source._lock) return !_source._disposed && SkipLocked(frames);
			}
			catch (InvalidOperationException ex)
			{
				Error = ex.Message;
				return false;
			}
		}

		/// <summary>The next frame, or null at the end of the recording (or once cancelled or failed).</summary>
		/// <param name="step">1 = every frame; N = decode N frames and return only the last (fast playback).</param>
		public VideoFrame? TryReadNextFrame(int step = 1)
		{
			if (_ct.IsCancellationRequested || Error is not null) return null;

			try
			{
				lock (_source._lock)
				{
					if (_source._disposed || !SkipLocked(step - 1)) return null;

					(VideoFrame Frame, long Index)? decoded = _source.DecodeNext(_next, _ct);
					if (decoded is not { } frame) return null;

					_next = frame.Index + 1;
					Position = TimeSpan.FromSeconds(frame.Index / _source.Fps);
					return frame.Frame;
				}
			}
			catch (InvalidOperationException ex)
			{
				Error = ex.Message;
				return null;
			}
		}

		/// <summary>Under the source's lock.</summary>
		private bool SkipLocked(int frames)
		{
			for (int i = 0; i < frames; i++)
			{
				if (_ct.IsCancellationRequested || !_source.SkipNext(_next, _ct)) return false;
				_next = _source._nextFrame;
			}

			return true;
		}
	}

	/// <summary>
	///     One open file's video: the shared decoder plus the conversion to BGRA at the preview size. A 360 recording's
	///     second lens (a stream of its own in an .insv) gets a decoder of its own, moved in step with the first; its
	///     lenses are converted to BGRA at full size and the FisheyeProjector makes the flat view from them.
	/// </summary>
	private sealed class DecoderSession : IDisposable
	{
		private readonly LibavStreamDecoder _decoder;
		private readonly LibavStreamDecoder? _secondLens;
		private readonly DualFisheye? _lenses;
		private readonly FisheyeProjector? _projector;
		private readonly double _fps;
		private readonly AVFrame* _transfer;
		private readonly AVFrame* _secondTransfer;
		private readonly AVFrame* _scaled;
		private SwsContext* _sws;
		// The lenses as BGRA (two pictures, or one twice as wide side by side) - native, 29 MB at 1920 px a lens.
		private byte* _lensPixels;

		private DecoderSession(LibavStreamDecoder decoder, LibavStreamDecoder? secondLens, DualFisheye? lenses, int segmentIndex, double fps)
		{
			_decoder = decoder;
			_secondLens = secondLens;
			_lenses = lenses;
			_projector = lenses is null ? null : new FisheyeProjector();
			_fps = fps;
			SegmentIndex = segmentIndex;
			_transfer = ffmpeg.av_frame_alloc();
			_secondTransfer = ffmpeg.av_frame_alloc();
			_scaled = ffmpeg.av_frame_alloc();
		}

		public int SegmentIndex { get; }
		public string? Hardware => _decoder.Hardware;
		public bool HasFrame => _decoder.HasFrame;

		public static DecoderSession Open(string path, int segmentIndex, double fps, DualFisheye? lenses)
		{
			LibavStreamDecoder decoder = LibavStreamDecoder.Open(path, AVMediaType.AVMEDIA_TYPE_VIDEO, true)
			                             ?? throw new InvalidOperationException($"{path} has no video stream.");
			LibavStreamDecoder? secondLens = null;
			try
			{
				if (lenses?.Layout == FisheyeLayout.TwoStreams)
				{
					secondLens = LibavStreamDecoder.Open(path, AVMediaType.AVMEDIA_TYPE_VIDEO, true, 1)
					             ?? throw new InvalidOperationException($"{path} has no second lens stream.");
				}

				return new DecoderSession(decoder, secondLens, lenses, segmentIndex, fps);
			}
			catch
			{
				secondLens?.Dispose();
				decoder.Dispose();
				throw;
			}
		}

		/// <summary>To the keyframe at or before the frame; the frames up to it still have to be decoded.</summary>
		public void Seek(long localFrame)
		{
			_decoder.Seek(localFrame / _fps);
			_secondLens?.Seek(localFrame / _fps);
		}

		/// <summary>The next frame - of both lenses, the second one decoded on until it's at the first one's frame.</summary>
		public bool Receive()
		{
			if (!_decoder.Receive()) return false;
			if (_secondLens is null) return true;

			long index = FrameIndex();
			while (!_secondLens.HasFrame || Math.Round(_secondLens.FrameSeconds() * _fps) < index)
			{
				if (!_secondLens.Receive())
					break;
			}

			return true;
		}

		/// <summary>The current frame's index within this file.</summary>
		public long FrameIndex()
		{
			return (long)Math.Round(_decoder.FrameSeconds() * _fps);
		}

		/// <summary>
		///     The current frame as BGRA at the given size, converted with the frame's own color matrix and range - for a 360
		///     recording the reframer's view of it, leveled for `recordingSeconds`.
		/// </summary>
		public void ConvertInto(byte[] destination, int width, int height, Reframer? reframer, double recordingSeconds)
		{
			AVFrame* source = InMemory(_decoder.Frame, _transfer);
			fixed (byte* target = destination)
			{
				if (_lenses is null || reframer is null)
				{
					Scale(source, target, destination.Length, width * 4, width, height);
					return;
				}

				int size = _lenses.LensSize;
				if (_lensPixels is null) _lensPixels = (byte*)NativeMemory.Alloc((nuint)(2L * size * size * 4));

				LensImage front, back;
				if (_secondLens is not null)
				{
					// The second stream is the front lens (v360's right half).
					long lensBytes = (long)size * size * 4;
					Scale(InMemory(_secondLens.Frame, _secondTransfer), _lensPixels, lensBytes, size * 4, size, size);
					Scale(source, _lensPixels + lensBytes, lensBytes, size * 4, size, size);
					front = new LensImage(_lensPixels, size * 4);
					back = new LensImage(_lensPixels + lensBytes, size * 4);
				}
				else
				{
					// Side by side, the front lens is on the left.
					Scale(source, _lensPixels, 2L * size * size * 4, size * 8, size * 2, size);
					front = new LensImage(_lensPixels, size * 8);
					back = new LensImage(_lensPixels + size * 4, size * 8);
				}

				_projector!.Project(front, back, _lenses, reframer.RotationAt(recordingSeconds), reframer.View.FovDegrees, target, width * 4,
					width, height);
			}
		}

		/// <summary>
		///     swscale straight into `target`: the frame borrows it (pinned or native, for the call) through a buffer whose
		///     free callback does nothing, instead of converting into a frame of its own and copying that over.
		/// </summary>
		private void Scale(AVFrame* source, byte* target, long bytes, int rowBytes, int width, int height)
		{
			if (_sws is null)
			{
				_sws = ffmpeg.sws_alloc_context();
				_sws->flags = (uint)SwsFlags.SWS_BILINEAR;
				_sws->threads = 0;
			}

			try
			{
				_scaled->width = width;
				_scaled->height = height;
				_scaled->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
				_scaled->color_range = AVColorRange.AVCOL_RANGE_JPEG;
				_scaled->buf[0] = ffmpeg.av_buffer_create(target, (ulong)bytes, BorrowedBuffer, null, 0);
				if (_scaled->buf[0] is null) throw new InvalidOperationException("FFmpeg could not wrap the preview frame.");
				_scaled->data[0] = target;
				_scaled->linesize[0] = rowBytes;
				LibavStreamDecoder.Check(ffmpeg.sws_scale_frame(_sws, _scaled, source), "convert the frame");
				if (_scaled->data[0] != target) throw new InvalidOperationException("swscale converted into a frame of its own.");
			}
			finally
			{
				ffmpeg.av_frame_unref(_scaled);
			}
		}

		/// <summary>A decoded frame in system memory - downloaded into `transfer` when it was decoded on the GPU.</summary>
		private static AVFrame* InMemory(AVFrame* frame, AVFrame* transfer)
		{
			if (frame->hw_frames_ctx is null) return frame;

			ffmpeg.av_frame_unref(transfer);
			LibavStreamDecoder.Check(ffmpeg.av_hwframe_transfer_data(transfer, frame, 0), "copy the frame from the GPU");
			LibavStreamDecoder.Check(ffmpeg.av_frame_copy_props(transfer, frame), "copy the frame's properties");
			return transfer;
		}

		// Kept in a static field so the delegate FFmpeg calls back into is never collected.
		private static readonly av_buffer_create_free BorrowedBuffer = (_, _) => { };

		public void Dispose()
		{
			AVFrame* transfer = _transfer;
			AVFrame* secondTransfer = _secondTransfer;
			AVFrame* scaled = _scaled;
			SwsContext* sws = _sws;
			ffmpeg.av_frame_free(&transfer);
			ffmpeg.av_frame_free(&secondTransfer);
			ffmpeg.av_frame_free(&scaled);
			ffmpeg.sws_free_context(&sws);
			NativeMemory.Free(_lensPixels);
			_lensPixels = null;
			_secondLens?.Dispose();
			_decoder.Dispose();
		}
	}
}
