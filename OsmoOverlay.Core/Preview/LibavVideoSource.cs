using FFmpeg.AutoGen;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     The preview's decoder, in-process through the FFmpeg shared libraries (FFmpeg.AutoGen, loaded by LibavLoader).
///     Each segment's file is opened once and kept open, so a seek is a demuxer seek plus decoding from the keyframe -
///     no process spawn and no re-reading of the file's index, which made the former ffmpeg process per seek take
///     1.3-1.7 s on a 4K Osmo file.
///     Decodes on the GPU where there is a decoder for it (DecoderSession.Open), else multi-threaded in software.
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
	// Frames before a one-frame back step that the same decode pass keeps, so the next steps back need no decoding.
	private const int BackStepFrames = 15;

	private readonly Lock _lock = new();
	private readonly FrameBufferPool _buffers = new(BackStepFrames + 6);
	private readonly List<Segment> _segments;
	private readonly List<DecoderSession> _sessions = [];
	private readonly Dictionary<long, byte[]> _backStepCache = [];
	private readonly int _width;
	private readonly int _height;
	private readonly long _totalFrames;
	private DecoderSession? _positioned;
	// The global frame the positioned session outputs next when decoding on.
	private long _nextFrame = -1;
	private long _lastReturned = -1;
	private bool _disposed;

	public LibavVideoSource(IReadOnlyList<PlaybackSegment> segments, double fps, int width, int height)
	{
		if (LibavLoader.TryLoad() is { } failure) throw new InvalidOperationException($"The preview can't decode video: {failure}");

		Fps = fps;
		_width = width;
		_height = height;

		_segments = new List<Segment>(segments.Count);
		double offset = 0;
		foreach (PlaybackSegment segment in segments)
		{
			var start = (long)Math.Round(offset * fps);
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
		var target = Math.Clamp(PreviewFrames.IndexAt(position.TotalSeconds, Fps), 0, _totalFrames - 1);
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
		if (_backStepCache.TryGetValue(target, out var cached)) return (Returned(Copy(cached), target), target);

		var (segmentIndex, localTarget) = Locate(target);
		Segment segment = _segments[segmentIndex];
		DecoderSession session = SessionFor(segmentIndex);

		var decodeOn = ReferenceEquals(session, _positioned) && target >= _nextFrame && target - _nextFrame <= DecodeAheadSeconds * Fps;
		if (!decodeOn)
		{
			var backStep = target == _lastReturned - 1;
			foreach (var buffer in _backStepCache.Values) _buffers.Return(buffer);
			_backStepCache.Clear();

			session.Seek(localTarget);
			_positioned = session;
			_nextFrame = -1;

			if (accuracy == SeekAccuracy.Keyframe)
			{
				if (!session.Receive()) throw new InvalidOperationException($"No frame after seeking to frame {target} of {segment.Path}.");

				var keyframe = segment.StartFrame + session.FrameIndex();
				_nextFrame = keyframe + 1;
				return (Returned(Convert(session), keyframe), keyframe);
			}

			if (backStep) return DecodeTo(session, segment, target, target - BackStepFrames, ct);
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
				var last = _nextFrame - 1;
				return (Returned(Convert(session), last), last);
			}

			var index = segment.StartFrame + session.FrameIndex();
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
			var index = segment.StartFrame + session.FrameIndex();
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

	private VideoFrame Returned(VideoFrame frame, long index)
	{
		_lastReturned = index;
		return frame;
	}

	private VideoFrame Convert(DecoderSession session)
	{
		var buffer = _buffers.Rent(_width * _height * 4);
		session.ConvertInto(buffer, _width, _height);
		return new VideoFrame(buffer, _width * 4, _width, _height);
	}

	private VideoFrame Copy(byte[] source)
	{
		var buffer = _buffers.Rent(source.Length);
		Buffer.BlockCopy(source, 0, buffer, 0, source.Length);
		return new VideoFrame(buffer, _width * 4, _width, _height);
	}

	private (int Segment, long LocalFrame) Locate(long frame)
	{
		for (var i = _segments.Count - 1; i > 0; i--)
			if (frame >= _segments[i].StartFrame)
				return (i, frame - _segments[i].StartFrame);

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

		session = DecoderSession.Open(_segments[segmentIndex].Path, segmentIndex, Fps);
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
	public sealed class PlaybackStream : IDisposable
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

		/// <summary>Why the stream stopped early, if a decode failed.</summary>
		public string? Error { get; private set; }

		/// <summary>The next frame, or null at the end of the recording (or once cancelled or failed).</summary>
		public VideoFrame? TryReadNextFrame()
		{
			if (_ct.IsCancellationRequested || Error is not null) return null;

			try
			{
				lock (_source._lock)
				{
					if (_source._disposed) return null;

					var decoded = _source.DecodeNext(_next, _ct);
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

		public void Dispose()
		{
		}
	}

	/// <summary>One open file: demuxer, decoder and the conversion to BGRA at the preview size.</summary>
	private sealed class DecoderSession : IDisposable
	{
		private readonly AVFormatContext* _format;
		private readonly AVCodecContext* _codec;
		private readonly AVPacket* _packet;
		private readonly AVFrame* _transfer;
		private readonly AVFrame* _scaled;
		private readonly int _stream;
		private readonly double _timeBase;
		private readonly long _startPts;
		private readonly double _fps;
		// Two frames taking turns, so the last decoded one survives a receive that hits the end of the file.
		private AVFrame* _frame;
		private AVFrame* _spare;
		private SwsContext* _sws;
		private bool _draining;

		private DecoderSession(AVFormatContext* format, AVCodecContext* codec, int stream, int segmentIndex, double fps, string? hardware)
		{
			_format = format;
			_codec = codec;
			_stream = stream;
			_fps = fps;
			SegmentIndex = segmentIndex;
			Hardware = hardware;

			AVStream* videoStream = format->streams[stream];
			_timeBase = ffmpeg.av_q2d(videoStream->time_base);
			_startPts = videoStream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : videoStream->start_time;

			_packet = ffmpeg.av_packet_alloc();
			_frame = ffmpeg.av_frame_alloc();
			_spare = ffmpeg.av_frame_alloc();
			_transfer = ffmpeg.av_frame_alloc();
			_scaled = ffmpeg.av_frame_alloc();
		}

		public int SegmentIndex { get; }
		public string? Hardware { get; }
		public bool HasFrame { get; private set; }

		public static DecoderSession Open(string path, int segmentIndex, double fps)
		{
			AVFormatContext* format = null;
			AVCodecContext* codec = null;
			try
			{
				Check(ffmpeg.avformat_open_input(&format, path, null, null), $"open {path}");

				AVCodec* decoder = null;
				var stream = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
				Check(stream, $"find the video stream of {path}");
				// The camera's audio, djmd/dbgi telemetry and timecode tracks are never needed here - discarded
				// streams aren't handed back by av_read_frame at all.
				for (var i = 0; i < format->nb_streams; i++)
					if (i != stream)
						format->streams[i]->discard = AVDiscard.AVDISCARD_ALL;

				codec = ffmpeg.avcodec_alloc_context3(decoder);
				Check(ffmpeg.avcodec_parameters_to_context(codec, format->streams[stream]->codecpar), "set up the decoder");
				codec->pkt_timebase = format->streams[stream]->time_base;

				var hardware = AttachHardwareDevice(codec, decoder);
				if (hardware is null)
				{
					codec->thread_count = 0;
					codec->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
				}

				Check(ffmpeg.avcodec_open2(codec, decoder, null), "open the decoder");
				return new DecoderSession(format, codec, stream, segmentIndex, fps, hardware);
			}
			catch
			{
				if (codec is not null) ffmpeg.avcodec_free_context(&codec);
				if (format is not null) ffmpeg.avformat_close_input(&format);
				throw;
			}
		}

		/// <summary>
		///     The first hardware decoder this platform has that also supports the codec. With hw_device_ctx set,
		///     libavcodec's default get_format picks the matching hardware format itself.
		/// </summary>
		private static string? AttachHardwareDevice(AVCodecContext* codec, AVCodec* decoder)
		{
			AVHWDeviceType[] candidates = OperatingSystem.IsWindows()
				? [AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2]
				: OperatingSystem.IsMacOS()
					? [AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX]
					: [AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA];

			foreach (AVHWDeviceType type in candidates)
			{
				if (!SupportsDevice(decoder, type)) continue;

				AVBufferRef* device = null;
				if (ffmpeg.av_hwdevice_ctx_create(&device, type, null, null, 0) < 0) continue;

				codec->hw_device_ctx = ffmpeg.av_buffer_ref(device);
				ffmpeg.av_buffer_unref(&device);
				return ffmpeg.av_hwdevice_get_type_name(type);
			}

			return null;
		}

		private static bool SupportsDevice(AVCodec* decoder, AVHWDeviceType type)
		{
			for (var i = 0;; i++)
			{
				AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(decoder, i);
				if (config is null) return false;
				if (config->device_type == type && (config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0)
					return true;
			}
		}

		/// <summary>To the keyframe at or before the frame; the frames up to it still have to be decoded.</summary>
		public void Seek(long localFrame)
		{
			var timestamp = _startPts + (long)Math.Round(localFrame / _fps / _timeBase);
			Check(ffmpeg.av_seek_frame(_format, _stream, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD), "seek");
			ffmpeg.avcodec_flush_buffers(_codec);
			_draining = false;
			HasFrame = false;
		}

		/// <summary>Decodes the next frame; false at the end of the file (the previous frame stays available).</summary>
		public bool Receive()
		{
			while (true)
			{
				var result = ffmpeg.avcodec_receive_frame(_codec, _spare);
				if (result == 0)
				{
					AVFrame* decoded = _spare;
					_spare = _frame;
					_frame = decoded;
					HasFrame = true;
					return true;
				}

				if (result == ffmpeg.AVERROR_EOF || _draining) return false;
				if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN)) Check(result, "decode");

				result = ffmpeg.av_read_frame(_format, _packet);
				if (result == ffmpeg.AVERROR_EOF)
				{
					// Draining hands out the frames the decoder still holds, then the end of the file.
					ffmpeg.avcodec_send_packet(_codec, null);
					_draining = true;
					continue;
				}

				Check(result, "read");
				try
				{
					result = ffmpeg.avcodec_send_packet(_codec, _packet);
					// A damaged packet costs its own frame, not the whole preview.
					if (result < 0 && result != ffmpeg.AVERROR(ffmpeg.EAGAIN) && result != ffmpeg.AVERROR_INVALIDDATA) Check(result, "decode");
				}
				finally
				{
					ffmpeg.av_packet_unref(_packet);
				}
			}
		}

		/// <summary>The current frame's index within this file.</summary>
		public long FrameIndex()
		{
			var pts = _frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? _frame->best_effort_timestamp : _frame->pts;
			return (long)Math.Round((pts - _startPts) * _timeBase * _fps);
		}

		/// <summary>The current frame as BGRA at the given size, converted with the frame's own color matrix and range.</summary>
		public void ConvertInto(byte[] destination, int width, int height)
		{
			AVFrame* source = _frame;
			if (source->hw_frames_ctx is not null)
			{
				ffmpeg.av_frame_unref(_transfer);
				Check(ffmpeg.av_hwframe_transfer_data(_transfer, source, 0), "copy the frame from the GPU");
				Check(ffmpeg.av_frame_copy_props(_transfer, source), "copy the frame's properties");
				source = _transfer;
			}

			if (_sws is null)
			{
				_sws = ffmpeg.sws_alloc_context();
				_sws->flags = (uint)SwsFlags.SWS_BILINEAR;
				_sws->threads = 0;
			}

			if (_scaled->buf[0] is null || _scaled->width != width || _scaled->height != height)
			{
				ffmpeg.av_frame_unref(_scaled);
				_scaled->width = width;
				_scaled->height = height;
				_scaled->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
				Check(ffmpeg.av_frame_get_buffer(_scaled, 0), "allocate the preview frame");
			}

			_scaled->color_range = AVColorRange.AVCOL_RANGE_JPEG;
			Check(ffmpeg.sws_scale_frame(_sws, _scaled, source), "convert the frame");

			var rowBytes = width * 4;
			var stride = _scaled->linesize[0];
			fixed (byte* target = destination)
			{
				if (stride == rowBytes)
					Buffer.MemoryCopy(_scaled->data[0], target, destination.Length, (long)rowBytes * height);
				else
					for (var y = 0; y < height; y++)
						Buffer.MemoryCopy(_scaled->data[0] + (long)y * stride, target + (long)y * rowBytes, rowBytes, rowBytes);
			}
		}

		public void Dispose()
		{
			AVFrame* frame = _frame;
			AVFrame* spare = _spare;
			AVFrame* transfer = _transfer;
			AVFrame* scaled = _scaled;
			AVPacket* packet = _packet;
			AVCodecContext* codec = _codec;
			AVFormatContext* format = _format;
			SwsContext* sws = _sws;

			ffmpeg.av_frame_free(&frame);
			ffmpeg.av_frame_free(&spare);
			ffmpeg.av_frame_free(&transfer);
			ffmpeg.av_frame_free(&scaled);
			ffmpeg.av_packet_free(&packet);
			ffmpeg.sws_free_context(&sws);
			ffmpeg.avcodec_free_context(&codec);
			ffmpeg.avformat_close_input(&format);
		}

		private static void Check(int result, string action)
		{
			if (result >= 0) return;

			const int size = 256;
			var message = stackalloc byte[size];
			ffmpeg.av_strerror(result, message, size);
			throw new InvalidOperationException($"FFmpeg could not {action}: {new string((sbyte*)message)}");
		}
	}
}
