namespace OsmoOverlay.Core.Preview;

/// <summary>One decoded preview frame, BGRA at the preview size.</summary>
public sealed record VideoFrame(byte[] Bgra, int Stride, int Width, int Height);

/// <summary>One physical file on the combined preview timeline.</summary>
public sealed record PlaybackSegment(string Path, double DurationSeconds);

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
