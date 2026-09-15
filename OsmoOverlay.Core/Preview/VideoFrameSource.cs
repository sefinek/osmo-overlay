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
	private readonly IReadOnlyList<(string Path, double DurationSeconds, double StartOffsetSeconds)> _segments;
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
		using CancellationTokenRegistration registration = ct.Register(() =>
		{
			try
			{
				if (!process.HasExited) process.Kill(true);
			}
			catch (InvalidOperationException)
			{
			}
		});

		Task<string> stderrTask = process.StandardError.ReadToEndAsync();

		var buffer = new byte[FrameByteCount];
		var read = ReadFully(process.StandardOutput.BaseStream, buffer);
		process.WaitForExit();

		if (ct.IsCancellationRequested) return (0, buffer, "", true);

		return (read, buffer, stderrTask.GetAwaiter().GetResult(), false);
	}

	/// <summary>
	///     Continuous decode from any point on the combined timeline through to the end. A single
	///     remaining segment is played the same way as before (a plain -ss seek); when more than one
	///     segment remains, they're stitched via the concat demuxer's per-entry "inpoint" (seeks into
	///     the first segment, then streams the rest in full) so one ffmpeg process decodes straight
	///     through file boundaries instead of Play having to restart on every segment change.
	/// </summary>
	public VideoPlaybackStream OpenPlaybackStream(TimeSpan from)
	{
		TimeSpan start = ClampGlobal(from);
		(var index, TimeSpan local) = Locate(start);

		if (_segments.Count - index == 1)
		{
			Process single = StartFfmpeg(_segments[index].Path, local, false);
			return new VideoPlaybackStream(single, start, Fps, _width, _height, null);
		}

		IEnumerable<(string Path, double?)> entries = _segments.Skip(index).Select((s, i) => (s.Path, i == 0 ? (double?)local.TotalSeconds : null));
		var listPath = ConcatListWriter.Write(entries);
		Process process = StartFfmpegConcat(listPath);
		return new VideoPlaybackStream(process, start, Fps, _width, _height, listPath);
	}

	private Process StartFfmpeg(string inputPath, TimeSpan position, bool singleFrame)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffmpeg");

		psi.ArgumentList.Add("-hide_banner");
		psi.ArgumentList.Add("-ss");
		psi.ArgumentList.Add(position.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
		psi.ArgumentList.Add("-hwaccel");
		psi.ArgumentList.Add("auto");
		psi.ArgumentList.Add("-i");
		psi.ArgumentList.Add(inputPath);
		AppendCommonDecodeArgs(psi, singleFrame);

		return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
	}

	private Process StartFfmpegConcat(string listPath)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffmpeg");

		psi.ArgumentList.Add("-hide_banner");
		psi.ArgumentList.Add("-hwaccel");
		psi.ArgumentList.Add("auto");
		psi.ArgumentList.Add("-f");
		psi.ArgumentList.Add("concat");
		psi.ArgumentList.Add("-safe");
		psi.ArgumentList.Add("0");
		psi.ArgumentList.Add("-i");
		psi.ArgumentList.Add(listPath);
		AppendCommonDecodeArgs(psi, false);

		return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
	}

	private void AppendCommonDecodeArgs(ProcessStartInfo psi, bool singleFrame)
	{
		psi.ArgumentList.Add("-an");
		psi.ArgumentList.Add("-sn");
		if (singleFrame)
		{
			psi.ArgumentList.Add("-frames:v");
			psi.ArgumentList.Add("1");
		}

		psi.ArgumentList.Add("-vf");
		psi.ArgumentList.Add($"scale={_width}:{_height}");
		psi.ArgumentList.Add("-pix_fmt");
		psi.ArgumentList.Add("bgra");
		psi.ArgumentList.Add("-f");
		psi.ArgumentList.Add("rawvideo");
		psi.ArgumentList.Add("-loglevel");
		psi.ArgumentList.Add("error");
		psi.ArgumentList.Add("pipe:1");
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
}

public sealed class VideoPlaybackStream : IDisposable
{
	private readonly string? _concatListPath;
	private readonly double _fps;
	private readonly int _height;
	private readonly Process _process;
	private readonly Stream _stdout;
	private readonly int _width;
	private long _framesRead;

	internal VideoPlaybackStream(Process process, TimeSpan startPosition, double fps, int width, int height,
		string? concatListPath)
	{
		_process = process;
		_stdout = process.StandardOutput.BaseStream;
		StderrTask = process.StandardError.ReadToEndAsync();
		StartPosition = startPosition;
		Position = startPosition;
		_fps = fps;
		_width = width;
		_height = height;
		_concatListPath = concatListPath;
	}

	public TimeSpan StartPosition { get; }
	public TimeSpan Position { get; private set; }
	public Task<string> StderrTask { get; }

	public void Dispose()
	{
		try
		{
			if (!_process.HasExited) _process.Kill(true);
		}
		catch (InvalidOperationException)
		{
		}

		_process.Dispose();

		if (_concatListPath is not null)
			try
			{
				File.Delete(_concatListPath);
			}
			catch
			{
				// Best-effort: a stray temp file is harmless, not worth failing over.
			}
	}

	public VideoFrame? TryReadNextFrame()
	{
		var buffer = new byte[_width * _height * 4];
		var read = VideoFrameSource.ReadFully(_stdout, buffer);
		if (read < buffer.Length) return null;

		Position = StartPosition + TimeSpan.FromSeconds(_framesRead / _fps);
		_framesRead++;
		return new VideoFrame(buffer, _width * 4, _width, _height);
	}
}
