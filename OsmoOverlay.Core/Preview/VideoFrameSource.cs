using System.Diagnostics;
using System.Globalization;

namespace OsmoOverlay.Core.Preview;

public sealed record VideoFrame(byte[] Bgra, int Stride, int Width, int Height);

public sealed class VideoFrameSource
{
	private readonly int _height;
	private readonly string _inputPath;
	private readonly int _width;

	private VideoFrameSource(string inputPath, TimeSpan duration, double fps, int width, int height)
	{
		_inputPath = inputPath;
		Duration = duration;
		Fps = fps;
		_width = width;
		_height = height;
	}

	public TimeSpan Duration { get; }
	public double Fps { get; }

	private int FrameByteCount => _width * _height * 4;

	public static VideoFrameSource Open(string inputPath, double durationSeconds, double fps, int width, int height)
	{
		return new VideoFrameSource(inputPath, TimeSpan.FromSeconds(durationSeconds), fps, width, height);
	}

	/// <summary>
	///     Decodes a single frame, or returns null if <paramref name="ct" /> is cancelled first - a
	///     scrub seek getting superseded by a newer one is expected, frequent behavior, not an
	///     exceptional one, so cancellation is a plain cooperative check rather than a thrown exception.
	///     Right near the end of a file, exactly how much margin ffmpeg needs before it can actually
	///     decode a frame varies by encoder/keyframe layout - rather than guess one fixed epsilon,
	///     this backs off further and retries a few times before giving up.
	/// </summary>
	public VideoFrame? GetFrame(TimeSpan position, CancellationToken ct = default)
	{
		var attemptPosition = Clamp(position);
		var backoff = TimeSpan.FromMilliseconds(200);
		var lastStderr = "";

		// Retrying only helps the near-EOF case the docstring describes; a genuinely broken file
		// fails at every position, so retrying it just multiplies ffmpeg spawns before the same
		// error surfaces - and this runs under PreviewPlayer's lock, so that delay blocks playback too.
		var maxAttempts = Duration - attemptPosition <= TimeSpan.FromSeconds(1) ? 5 : 1;

		for (var attempt = 0; attempt < maxAttempts; attempt++)
		{
			(var read, byte[] buffer, var stderr, var cancelled) = DecodeOneFrame(attemptPosition, ct);
			if (cancelled) return null;
			if (read == buffer.Length) return new VideoFrame(buffer, _width * 4, _width, _height);

			lastStderr = stderr;
			if (attempt == maxAttempts - 1) break;

			TimeSpan next = attemptPosition - backoff;
			attemptPosition = next < TimeSpan.Zero ? TimeSpan.Zero : next;
		}

		throw new InvalidOperationException($"ffmpeg produced no frame near {position}: {lastStderr}");
	}

	private (int Read, byte[] Buffer, string Stderr, bool Cancelled) DecodeOneFrame(TimeSpan position,
		CancellationToken ct)
	{
		using Process process = StartFfmpeg(position, true);
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

	public VideoPlaybackStream OpenPlaybackStream(TimeSpan from)
	{
		TimeSpan start = Clamp(from);
		Process process = StartFfmpeg(start, false);
		return new VideoPlaybackStream(process, start, Fps, _width, _height);
	}

	private Process StartFfmpeg(TimeSpan position, bool singleFrame)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffmpeg");

		psi.ArgumentList.Add("-hide_banner");
		psi.ArgumentList.Add("-ss");
		psi.ArgumentList.Add(position.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
		psi.ArgumentList.Add("-hwaccel");
		psi.ArgumentList.Add("auto");
		psi.ArgumentList.Add("-i");
		psi.ArgumentList.Add(_inputPath);
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

		return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
	}

	private TimeSpan Clamp(TimeSpan position)
	{
		if (position < TimeSpan.Zero) return TimeSpan.Zero;

		// Seeking to the exact reported duration (or beyond) leaves ffmpeg with no frame after
		// that point to decode, so the last seekable position stays one frame short of the end.
		TimeSpan lastFrame = Duration - TimeSpan.FromSeconds(1.0 / Fps);
		if (lastFrame < TimeSpan.Zero) lastFrame = TimeSpan.Zero;

		return position > lastFrame ? lastFrame : position;
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
	private readonly double _fps;
	private readonly int _height;
	private readonly Process _process;
	private readonly Stream _stdout;
	private readonly int _width;
	private long _framesRead;

	internal VideoPlaybackStream(Process process, TimeSpan startPosition, double fps, int width, int height)
	{
		_process = process;
		_stdout = process.StandardOutput.BaseStream;
		StderrTask = process.StandardError.ReadToEndAsync();
		StartPosition = startPosition;
		Position = startPosition;
		_fps = fps;
		_width = width;
		_height = height;
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
