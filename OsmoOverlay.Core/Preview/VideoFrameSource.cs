using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Core.Preview;

public sealed record VideoFrame(byte[] Bgra, int Stride, int Width, int Height);

/// <summary>One physical file on the combined preview timeline - just what VideoFrameSource needs.</summary>
public sealed record PlaybackSegment(string Path, double DurationSeconds);

public sealed class VideoFrameSource
{
	private readonly int _height;
	private readonly List<(string Path, double DurationSeconds, double StartOffsetSeconds)> _segments;
	private readonly int _width;

	private VideoFrameSource(IReadOnlyList<PlaybackSegment> segments, double fps, int width, int height)
	{
		var withOffsets = new List<(string Path, double DurationSeconds, double StartOffsetSeconds)>(segments.Count);
		var offset = 0.0;
		foreach (PlaybackSegment segment in segments)
		{
			withOffsets.Add((segment.Path, segment.DurationSeconds, offset));
			offset += segment.DurationSeconds;
		}

		_segments = withOffsets;
		Duration = TimeSpan.FromSeconds(offset);
		Fps = fps;
		_width = width;
		_height = height;
	}

	public TimeSpan Duration { get; }
	public double Fps { get; }

	private int FrameByteCount => _width * _height * 4;

	public static VideoFrameSource Open(IReadOnlyList<PlaybackSegment> segments, double fps, int width, int height)
	{
		return new VideoFrameSource(segments, fps, width, height);
	}

	/// <summary>
	///     Decodes a single frame, or returns null if <paramref name="ct" /> is cancelled first - a
	///     scrub seek getting superseded by a newer one is expected, frequent behavior, not an
	///     exceptional one, so cancellation is a plain cooperative check rather than a thrown exception.
	///     Right near the end of the LAST segment, exactly how much margin ffmpeg needs before it can
	///     actually decode a frame varies by encoder/keyframe layout - rather than guess one fixed
	///     epsilon, this backs off further and retries a few times before giving up. An intermediate
	///     segment boundary is not a real end of stream, so no retry is needed there.
	/// </summary>
	public VideoFrame? GetFrame(TimeSpan position, CancellationToken ct = default)
	{
		(var index, TimeSpan local) = Locate(ClampGlobal(position));
		var (path, durationSeconds, _) = _segments[index];
		var isLastSegment = index == _segments.Count - 1;

		TimeSpan attemptPosition = ClampLocal(local, durationSeconds);
		TimeSpan backoff = TimeSpan.FromMilliseconds(200);
		var lastStderr = "";

		var maxAttempts = isLastSegment && durationSeconds - attemptPosition.TotalSeconds <= 1.0 ? 5 : 1;

		for (var attempt = 0; attempt < maxAttempts; attempt++)
		{
			var (read, buffer, stderr, cancelled) = DecodeOneFrame(path, attemptPosition, ct);
			if (cancelled) return null;
			if (read == buffer.Length) return new VideoFrame(buffer, _width * 4, _width, _height);

			lastStderr = stderr;
			if (attempt == maxAttempts - 1) break;

			TimeSpan next = attemptPosition - backoff;
			attemptPosition = next < TimeSpan.Zero ? TimeSpan.Zero : next;
		}

		throw new InvalidOperationException(
			$"ffmpeg produced no frame near {position} ({path} @ {attemptPosition}): {lastStderr}");
	}

	private (int Read, byte[] Buffer, string Stderr, bool Cancelled) DecodeOneFrame(string path, TimeSpan position,
		CancellationToken ct)
	{
		using Process process = StartFfmpeg(path, position, true);
		using CancellationTokenRegistration registration = ct.Register(() => KillIfRunning(process));

		Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

		var buffer = new byte[FrameByteCount];
		var read = ReadFully(process.StandardOutput.BaseStream, buffer);
		process.WaitForExit();

		return ct.IsCancellationRequested ? (0, buffer, "", true) : (read, buffer, stderrTask.GetAwaiter().GetResult(), false);
	}

	/// <summary>
	///     Continuous decode from any point on the combined timeline through to the end. A single
	///     remaining segment is played the same way as before (a plain -ss seek). When more than one
	///     segment remains and the start position falls exactly at a segment's own beginning (Play
	///     from 0, or resuming right after a segment boundary), the concat demuxer stitches all of
	///     them in one process cleanly - confirmed against a real recording, no seek involved there.
	///     Resuming into the *middle* of a non-last segment (a scrub-and-resume) is the one case the
	///     concat demuxer can't be trusted with - confirmed against a real recording that both its
	///     seek mechanisms (per-entry "inpoint" and a top-level -ss before -i) decode a stuck,
	///     repeated frame (or break reference frames outright) instead of actually seeking, with or
	///     without hwaccel. That case plays the partial first segment via its own plain single-file
	///     -ss process (the same reliable mechanism scrubbing already uses), then transparently hands
	///     off to a concat of the untouched remaining segments once that one naturally ends - see
	///     VideoPlaybackStream's nextStage.
	/// </summary>
	public VideoPlaybackStream OpenPlaybackStream(TimeSpan from, CancellationToken ct = default)
	{
		TimeSpan start = ClampGlobal(from);
		(var index, TimeSpan local) = Locate(start);

		if (_segments.Count - index == 1)
		{
			Process single = StartFfmpeg(_segments[index].Path, local, false);
			return new VideoPlaybackStream(single, null, start, Fps, _width, _height, null, ct);
		}

		if (local <= TimeSpan.Zero)
		{
			var listPath = ConcatListWriter.Write(_segments.Skip(index).Select(s => s.Path));
			Process process = StartFfmpegConcat(listPath);
			return new VideoPlaybackStream(process, null, start, Fps, _width, _height, listPath, ct);
		}

		Process firstStage = StartFfmpeg(_segments[index].Path, local, false);
		List<(string Path, double DurationSeconds, double StartOffsetSeconds)> tail = _segments.Skip(index + 1).ToList();
		VideoPlaybackStream.NextStageFactory nextStage = () => OpenTailStage(tail);
		return new VideoPlaybackStream(firstStage, nextStage, start, Fps, _width, _height, null, ct);
	}

	private (Process Process, string? ConcatListPath) OpenTailStage(
		List<(string Path, double DurationSeconds, double StartOffsetSeconds)> tail)
	{
		if (tail.Count == 1) return (StartFfmpeg(tail[0].Path, TimeSpan.Zero, false), null);

		var listPath = ConcatListWriter.Write(tail.Select(s => s.Path));
		return (StartFfmpegConcat(listPath), listPath);
	}

	/// <summary>
	///     singleFrame doubles as the "is this routine, high-frequency work" signal for logging: true is
	///     one grab per scrubbed frame (CreateHiddenQuiet - file log only, no GUI spam), false is a Play
	///     click starting continuous decode, a meaningful one-off worth CreateHidden's GUI-visible log line.
	/// </summary>
	private Process StartFfmpeg(string inputPath, TimeSpan position, bool singleFrame)
	{
		var args = new List<string>
		{
			"-hide_banner",
			"-ss", position.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
			"-hwaccel", "auto",
			"-i", inputPath
		};
		AppendCommonDecodeArgs(args, singleFrame);

		ProcessStartInfo psi = singleFrame
			? ProcessHelper.CreateHiddenQuiet("ffmpeg", [.. args])
			: ProcessHelper.CreateHidden("ffmpeg", [.. args]);

		return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
	}

	private Process StartFfmpegConcat(string listPath)
	{
		var args = new List<string>
		{
			"-hide_banner",
			"-hwaccel", "auto",
			"-f", "concat",
			"-safe", "0",
			"-i", listPath
		};
		AppendCommonDecodeArgs(args, false);

		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffmpeg", [.. args]);

		return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
	}

	private void AppendCommonDecodeArgs(List<string> args, bool singleFrame)
	{
		args.Add("-an");
		args.Add("-sn");
		if (singleFrame)
		{
			args.Add("-frames:v");
			args.Add("1");
		}

		args.AddRange([
			"-vf", $"scale={_width}:{_height}",
			"-pix_fmt", "bgra",
			"-f", "rawvideo",
			"-loglevel", "error",
			"pipe:1"
		]);
	}

	/// <summary>Finds which segment a combined-timeline position falls into, and its local offset there.</summary>
	private (int Index, TimeSpan Local) Locate(TimeSpan globalPosition)
	{
		var seconds = globalPosition.TotalSeconds;
		for (var i = 0; i < _segments.Count - 1; i++)
			if (seconds < _segments[i + 1].StartOffsetSeconds)
				return (i, TimeSpan.FromSeconds(seconds - _segments[i].StartOffsetSeconds));

		var last = _segments.Count - 1;
		return (last, TimeSpan.FromSeconds(seconds - _segments[last].StartOffsetSeconds));
	}

	private TimeSpan ClampGlobal(TimeSpan position)
	{
		if (position < TimeSpan.Zero) return TimeSpan.Zero;

		// Seeking to the exact reported duration (or beyond) leaves ffmpeg with no frame after
		// that point to decode, so the last seekable position stays one frame short of the end.
		TimeSpan lastFrame = Duration - TimeSpan.FromSeconds(1.0 / Fps);
		if (lastFrame < TimeSpan.Zero) lastFrame = TimeSpan.Zero;

		return position > lastFrame ? lastFrame : position;
	}

	private TimeSpan ClampLocal(TimeSpan local, double segmentDurationSeconds)
	{
		if (local < TimeSpan.Zero) return TimeSpan.Zero;

		var lastFrameSeconds = Math.Max(0, segmentDurationSeconds - 1.0 / Fps);
		return local.TotalSeconds > lastFrameSeconds ? TimeSpan.FromSeconds(lastFrameSeconds) : local;
	}

	internal static int ReadFully(Stream stream, byte[] buffer)
	{
		var offset = 0;
		while (offset < buffer.Length)
		{
			var read = stream.Read(buffer, offset, buffer.Length - offset);
			if (read == 0) break;
			offset += read;
		}

		return offset;
	}

	/// <summary>Best-effort kill shared by DecodeOneFrame's registration and VideoPlaybackStream.KillCurrentProcess.</summary>
	internal static void KillIfRunning(Process process)
	{
		try
		{
			if (!process.HasExited) process.Kill(true);
		}
		catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
		{
			// InvalidOperationException: the process exited in the gap between HasExited and Kill.
			// Win32Exception: the OS refused to terminate it (already exiting, access denied, etc.) -
			// this is a best-effort cleanup, not something worth failing playback/decode over.
		}
	}
}

public sealed class VideoPlaybackStream : IDisposable
{
	/// <summary>Lazily opens the next process once the current one naturally runs out - see TryReadNextFrame.</summary>
	internal delegate (Process Process, string? ConcatListPath) NextStageFactory();

	private readonly CancellationToken _ct;
	private readonly double _fps;
	private readonly int _height;
	private readonly int _width;
	private CancellationTokenRegistration _cancellationRegistration;
	private string? _concatListPath;
	private long _framesRead;
	private NextStageFactory? _nextStage;
	private Process _process;
	private Stream _stdout;

	// The registration targets the _process field itself (not a captured local), so it stays
	// correct across AdvanceToNextStage swapping which process is "current" - no need to
	// re-register per stage. Mirrors DecodeOneFrame's kill-on-cancel: TryReadNextFrame's stdout
	// read is a plain blocking Stream.Read with no cancellation of its own, held under
	// PreviewPlayer._lock - if ffmpeg ever stops producing bytes mid-stream without exiting (a
	// wedged decoder), that read would otherwise never return, and every other _lock-guarded
	// preview operation (Pause, scrub, dragging an element) would hang forever waiting on the same
	// lock. Killing the process here unblocks the read (as an early EOF) as soon as ct is cancelled.
	internal VideoPlaybackStream(Process process, NextStageFactory? nextStage, TimeSpan startPosition, double fps,
		int width, int height, string? concatListPath, CancellationToken ct)
	{
		_process = process;
		_nextStage = nextStage;
		_stdout = process.StandardOutput.BaseStream;
		StderrTask = process.StandardError.ReadToEndAsync();
		StartPosition = startPosition;
		Position = startPosition;
		_fps = fps;
		_width = width;
		_height = height;
		_concatListPath = concatListPath;
		_ct = ct;
		_cancellationRegistration = ct.Register(KillCurrentProcess);
	}

	public TimeSpan StartPosition { get; }
	public TimeSpan Position { get; private set; }
	public Task<string> StderrTask { get; private set; }

	public void Dispose()
	{
		_cancellationRegistration.Dispose();
		KillCurrentProcess();
		_process.Dispose();
		DeleteListFile();
	}

	private void KillCurrentProcess()
	{
		VideoFrameSource.KillIfRunning(_process);
	}

	private void DeleteListFile()
	{
		if (_concatListPath is null) return;

		try
		{
			File.Delete(_concatListPath);
		}
		catch
		{
			// Best-effort: a stray temp file is harmless, not worth failing over.
		}

		_concatListPath = null;
	}

	/// <summary>
	///     Position keeps counting frames delivered since StartPosition regardless of which
	///     underlying process is providing them, so the swap to the next stage (see
	///     VideoFrameSource.OpenPlaybackStream) is seamless to the caller - it just looks like the
	///     stream kept going.
	/// </summary>
	public VideoFrame? TryReadNextFrame()
	{
		var buffer = new byte[_width * _height * 4];
		var read = VideoFrameSource.ReadFully(_stdout, buffer);

		if (read < buffer.Length)
		{
			if (_nextStage is null || _ct.IsCancellationRequested) return null;

			NextStageFactory factory = _nextStage;
			_nextStage = null;

			KillCurrentProcess();
			_process.Dispose();
			DeleteListFile();

			(Process process, string? listPath) = factory();
			_process = process;
			_stdout = process.StandardOutput.BaseStream;
			StderrTask = process.StandardError.ReadToEndAsync();
			_concatListPath = listPath;

			return TryReadNextFrame();
		}

		Position = StartPosition + TimeSpan.FromSeconds(_framesRead / _fps);
		_framesRead++;
		return new VideoFrame(buffer, _width * 4, _width, _height);
	}
}
