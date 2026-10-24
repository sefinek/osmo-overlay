using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     One video of the comparison: its own decoder (on the GPU where there is one) at the size its tile is shown, and the
///     frames waiting for the common clock (Frames). The video is one file - an exported render, not a recording with
///     telemetry of its own.
/// </summary>
public sealed class CompareTile : IDisposable
{
	private CompareTile(string path, SourceInfo source, long fileSizeBytes, double fps, int width, int height, FrameBufferPool pool,
		LibavVideoSource video, CompareFrameFeed frames, ExportMarkerInfo? marker)
	{
		Path = path;
		Source = source;
		FileSizeBytes = fileSizeBytes;
		Fps = fps;
		Width = width;
		Height = height;
		Pool = pool;
		Video = video;
		Frames = frames;
		Marker = marker;
	}

	public string Path { get; }

	/// <summary>The file as ffprobe describes it (its size, frame rate and codec).</summary>
	public SourceInfo Source { get; }

	/// <summary>The file's exact size in bytes.</summary>
	public long FileSizeBytes { get; }

	public double Fps { get; }

	/// <summary>The size frames are decoded at - the file's own, or narrower to fit its tile.</summary>
	public int Width { get; }

	public int Height { get; }

	public CompareFrameFeed Frames { get; }

	/// <summary>What the file says about the OsmoOverlay that rendered it - null when it says nothing (another program's file, or the tag turned off).</summary>
	public ExportMarkerInfo? Marker { get; }

	/// <summary>Why this video stopped playing early, if it did.</summary>
	public string? Error { get; internal set; }

	public TimeSpan Duration => Video.Duration;

	internal FrameBufferPool Pool { get; }

	internal LibavVideoSource Video { get; }

	/// <param name="maxWidth">The widest the picture needs decoding - a file wider than its tile is scaled down while it is decoded.</param>
	internal static CompareTile Open(string path, int maxWidth, Func<double> clockSeconds)
	{
		SourceInfo source = SourceProbe.Probe(path, false);
		(int num, int den) = FfmpegPipeline.ParseFrameRate(source.Video.FrameRate);
		double fps = num / (double)den;
		long frameCount = source.Video.FrameCount ?? (long)Math.Round(source.DurationSeconds * fps);
		if (frameCount < 1) throw new InvalidOperationException($"No frames in {path}.");

		int width = Math.Max(2, Math.Min(source.Video.Width, maxWidth) & ~1);
		int height = Math.Max(2, (int)Math.Round(source.Video.Height * (double)width / source.Video.Width) & ~1);
		var pool = new FrameBufferPool(8);
		var video = new LibavVideoSource([new PlaybackSegment(path, source.DurationSeconds, frameCount)], fps, width, height, pool, null);
		try
		{
			return new CompareTile(path, source, new FileInfo(path).Length, fps, width, height, pool, video, new CompareFrameFeed(clockSeconds), ExportMarker.Read(path));
		}
		catch
		{
			video.Dispose();
			throw;
		}
	}

	public void Dispose()
	{
		Frames.SetPlaying(false);
		Frames.Clear();
		Video.Dispose();
	}
}
