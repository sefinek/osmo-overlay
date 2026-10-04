using OsmoOverlay.Core.Ffmpeg;

namespace OsmoOverlay.Core.Preview;

/// <summary>One decoded preview frame, BGRA (Width * 4 bytes a row) at the preview size.</summary>
public sealed record VideoFrame(byte[] Bgra, int Width, int Height);

/// <summary>
///     One physical file on the combined preview timeline: its video joins the next by FrameCount, as the render's does (each
///     piece's frames numbered on), its audio by DurationSeconds (the container's - where its audio ends), as the render's
///     concat input does.
/// </summary>
public sealed record PlaybackSegment(string Path, double DurationSeconds, long FrameCount)
{
	/// <summary>A loaded recording's files, in order - what every preview decoder opens.</summary>
	public static List<PlaybackSegment> Of(FileSummary summary)
	{
		return
		[
			.. summary.InputPaths.Select((path, i) => new PlaybackSegment(path, summary.SegmentDurationsSeconds[i], summary.SegmentFrameCounts[i]))
		];
	}

	public static List<PlaybackSegment> Of(IReadOnlyList<VideoSegment> segments)
	{
		return [.. segments.Select(s => new PlaybackSegment(s.InputPath, s.Source.DurationSeconds, s.FrameCount()))];
	}
}

public enum SeekAccuracy
{
	/// <summary>The exact frame at the position - what a paused preview, frame stepping and cut marks need.</summary>
	Exact,

	/// <summary>The keyframe at or before the position - fast enough to follow a timeline drag live.</summary>
	Keyframe
}

public static class PreviewFrames
{
	/// <summary>
	///     The frame on screen at a timeline position: the first one starting at/after it, with a quarter frame of
	///     slack - positions set by frame stepping sit a quarter frame before their frame (see the GUI's SeekToFrame),
	///     and a dragged position a little past a frame's start still means that frame. The one rule the GUI (which
	///     frame a cut mark lands on) and the decoder (which frame gets shown) share.
	/// </summary>
	public static long IndexAt(double seconds, double fps)
	{
		return Math.Max(0, (long)Math.Ceiling(seconds * fps - 0.25 - 1e-6));
	}
}
