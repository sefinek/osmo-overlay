namespace OsmoOverlay.Core;

/// <summary>Frames [Start, End) of the recording, on the combined timeline of all segments.</summary>
public readonly record struct FrameRange(long Start, long End)
{
	public long Length => End - Start;

	/// <summary>Exact frame times - RenderPlan.Resolve rounds them straight back to these frames.</summary>
	public TimeRange ToTimeRange(double fps)
	{
		return new TimeRange(Start / fps, End / fps);
	}
}

/// <summary>
///     The parts cut out of a recording, frame-exact, as the GUI edits them: the only model there is "what gets
///     removed" - trimming the start is a cut from frame 0, trimming the end a cut to the last frame. Every
///     operation works on and returns a normalized list: sorted, inside the recording, overlapping or touching
///     cuts merged.
/// </summary>
public static class CutList
{
	public static List<FrameRange> Normalize(IEnumerable<FrameRange> cuts, long totalFrames)
	{
		List<FrameRange> merged = [];
		foreach (FrameRange cut in cuts
			         .Select(c => new FrameRange(Math.Clamp(c.Start, 0, totalFrames), Math.Clamp(c.End, 0, totalFrames)))
			         .Where(c => c.Length > 0)
			         .OrderBy(c => c.Start))
		{
			if (merged.Count > 0 && cut.Start <= merged[^1].End)
				merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, cut.End) };
			else
				merged.Add(cut);
		}

		return merged;
	}

	public static List<FrameRange> Add(IEnumerable<FrameRange> cuts, FrameRange cut, long totalFrames)
	{
		return Normalize([.. cuts, cut], totalFrames);
	}

	public static long RemovedFrames(IEnumerable<FrameRange> cuts, long totalFrames)
	{
		return Normalize(cuts, totalFrames).Sum(c => c.Length);
	}

	public static bool RemovesEverything(IEnumerable<FrameRange> cuts, long totalFrames)
	{
		return RemovedFrames(cuts, totalFrames) >= totalFrames;
	}

	public static List<TimeRange> ToTimeRanges(IEnumerable<FrameRange> cuts, double fps)
	{
		return [.. cuts.Select(c => c.ToTimeRange(fps))];
	}
}
