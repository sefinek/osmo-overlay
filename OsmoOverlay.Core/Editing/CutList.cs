namespace OsmoOverlay.Core;

/// <summary>Frames [Start, End) of the recording, on the combined timeline of all segments.</summary>
public readonly record struct FrameRange(long Start, long End, CutTransition? Transition = null)
{
	public long Length => End - Start;

	/// <summary>Exact frame times - RenderPlan.Resolve rounds them straight back to these frames.</summary>
	public TimeRange ToTimeRange(double fps)
	{
		return new TimeRange(Start / fps, End / fps, Transition);
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
			         .Select(c => c with { Start = Math.Clamp(c.Start, 0, totalFrames), End = Math.Clamp(c.End, 0, totalFrames) })
			         .Where(c => c.Length > 0)
			         .OrderBy(c => c.Start))
		{
			if (merged.Count > 0 && cut.Start <= merged[^1].End)
			{
				// The join is at the merged cut's end, so the cut that reaches it decides the transition (an unset one keeps the other's).
				FrameRange last = merged[^1];
				merged[^1] = cut.End > last.End ? last with { End = cut.End, Transition = cut.Transition ?? last.Transition } : last;
			}
			else
				merged.Add(cut);
		}

		return merged;
	}

	public static List<FrameRange> Add(IEnumerable<FrameRange> cuts, FrameRange cut, long totalFrames)
	{
		return Normalize([.. cuts, cut], totalFrames);
	}

	/// <summary>
	///     The normalized list with its index-th cut replaced by one with new edges (dragged on the timeline) - an edge
	///     dragged past the other one swaps them, a cut shrunk to nothing is removed, one dragged over its neighbours
	///     merges with them.
	/// </summary>
	public static List<FrameRange> Resize(IEnumerable<FrameRange> cuts, int index, long start, long end, long totalFrames)
	{
		List<FrameRange> normalized = Normalize(cuts, totalFrames);
		if (index < 0 || index >= normalized.Count) return normalized;

		normalized[index] = normalized[index] with { Start = Math.Min(start, end), End = Math.Max(start, end) };
		return Normalize(normalized, totalFrames);
	}

	public static List<FrameRange> Remove(IEnumerable<FrameRange> cuts, int index, long totalFrames)
	{
		List<FrameRange> normalized = Normalize(cuts, totalFrames);
		if (index >= 0 && index < normalized.Count) normalized.RemoveAt(index);
		return normalized;
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
