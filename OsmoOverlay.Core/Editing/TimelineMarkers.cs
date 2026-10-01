namespace OsmoOverlay.Core;

/// <summary>
///     The points on the recording the preview can jump between (Up/Down): its first and last frame, both edges of
///     every cut and of the In/Out selection, and where GPS signal loss starts and ends - all as frames.
/// </summary>
public static class TimelineMarkers
{
	public static SortedSet<long> Collect(long totalFrames, IEnumerable<FrameRange> cuts, FrameRange? selection,
		IEnumerable<FrameRange> gpsLoss)
	{
		SortedSet<long> markers = [0, Math.Max(0, totalFrames - 1)];
		foreach (FrameRange range in cuts.Concat(gpsLoss).Concat(selection is { } s ? [s] : []))
		{
			markers.Add(range.Start);
			// A range's end is the first frame after it - where the kept video, the selection's outside or the fix resume.
			markers.Add(range.End);
		}

		markers.RemoveWhere(m => m < 0 || m >= totalFrames);
		return markers;
	}

	public static long? Next(SortedSet<long> markers, long frame)
	{
		return markers.GetViewBetween(frame + 1, long.MaxValue) is { Count: > 0 } after ? after.Min : null;
	}

	public static long? Previous(SortedSet<long> markers, long frame)
	{
		return frame > 0 && markers.GetViewBetween(long.MinValue, frame - 1) is { Count: > 0 } before ? before.Max : null;
	}
}
