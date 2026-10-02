namespace OsmoOverlay.Core;

/// <summary>A stretch of the recording in seconds, e.g. a part to cut out - Transition is what a cut does where it joins the parts around it.</summary>
public sealed record TimeRange(double StartSeconds, double EndSeconds, CutTransition? Transition = null);

/// <summary>
///     Frames [SourceStartFrame, SourceStartFrame + FrameCount) of the recording, on the combined timeline of all segments.
///     TransitionIn/TransitionOut are those of the cuts this piece follows and precedes. A fade through a color happens
///     within the piece's own frames; an overlapping transition (CutTransition.Overlaps) instead shows this piece's first
///     OverlapIn frames together with the previous piece's last ones, so those frames take no time of their own in the output.
/// </summary>
public sealed record RenderPiece(
	long SourceStartFrame,
	long FrameCount,
	CutTransition? TransitionIn = null,
	CutTransition? TransitionOut = null,
	int OverlapIn = 0)
{
	public long SourceEndFrame => SourceStartFrame + FrameCount;
}

/// <summary>
///     Which frames of the recording end up in the output, as the pieces that are kept, in order - the output
///     is those pieces joined back to back. One piece is a plain trim (start/end); more mean parts were cut
///     out of the middle. IsPartial is anything short of the whole recording.
/// </summary>
public sealed record RenderPlan(IReadOnlyList<RenderPiece> Pieces, bool IsPartial)
{
	/// <summary>The output's length - every kept frame, less those two parts share in an overlapping transition.</summary>
	public long TotalFrames => Pieces.Sum(p => p.FrameCount - p.OverlapIn);

	/// <summary>
	///     Keeps [start, end) minus every cut, each rounded to the nearest frame; overlapping or touching cuts
	///     merge. frameLimit then caps the total, dropping frames from the end.
	/// </summary>
	public static RenderPlan Resolve(double? startSeconds, double? endSeconds, IReadOnlyList<TimeRange>? cutOuts, int? frameLimit,
		double fps, long sourceFrames)
	{
		long ToFrame(double seconds)
		{
			return Math.Clamp((long)Math.Round(seconds * fps), 0, sourceFrames);
		}

		long start = startSeconds is { } s ? ToFrame(s) : 0;
		long end = endSeconds is { } e ? ToFrame(e) : sourceFrames;
		if (end <= start)
		{
			throw new InvalidOperationException(
				$"The render range is empty ({TimeText.Format(start / fps)} - {TimeText.Format(end / fps)}) - the end must come after the start.");
		}

		List<RenderPiece> pieces = [];
		long cursor = start;
		CutTransition? joinTransition = null;

		// The cut just passed joins the piece before it to this one.
		void AddPiece(long from, long count)
		{
			var piece = new RenderPiece(from, count);
			if (joinTransition is not null && pieces.Count > 0)
			{
				pieces[^1] = pieces[^1] with { TransitionOut = joinTransition };
				piece = piece with { TransitionIn = joinTransition };
			}

			joinTransition = null;
			pieces.Add(piece);
		}

		foreach ((long cutStart, long cutEnd, CutTransition? transition) in (cutOuts ?? [])
		         .Select(c => (Start: ToFrame(c.StartSeconds), End: ToFrame(c.EndSeconds), c.Transition))
		         .Where(c => c.End > c.Start)
		         .OrderBy(c => c.Start))
		{
			if (cutEnd <= cursor) continue;
			if (cutStart >= end) break;
			if (cutStart > cursor)
			{
				AddPiece(cursor, cutStart - cursor);
				joinTransition = transition;
			}

			cursor = Math.Max(cursor, cutEnd);
		}

		if (cursor < end) AddPiece(cursor, end - cursor);
		if (pieces.Count == 0) throw new InvalidOperationException("The cuts remove the whole render range - nothing is left to render.");

		if (frameLimit is > 0 and var limit) pieces = CapFrames(pieces, limit);

		// Known only now, from the frames each piece has left.
		for (int i = 1; i < pieces.Count; i++)
		{
			if (pieces[i].TransitionIn is { Overlaps: true } overlapping)
				pieces[i] = pieces[i] with { OverlapIn = overlapping.OverlapFrames(fps, pieces[i - 1].FrameCount, pieces[i].FrameCount) };
		}

		bool whole = pieces is [{ SourceStartFrame: 0 } only] && only.FrameCount == sourceFrames;
		return new RenderPlan(pieces, !whole);
	}

	private static List<RenderPiece> CapFrames(List<RenderPiece> pieces, long limit)
	{
		List<RenderPiece> capped = [];
		foreach (RenderPiece piece in pieces)
		{
			if (limit <= 0) break;
			capped.Add(piece with { FrameCount = Math.Min(piece.FrameCount, limit) });
			limit -= piece.FrameCount;
		}

		// The output ends here, not at a cut - nothing follows the last piece to fade or overlap into.
		capped[^1] = capped[^1] with { TransitionOut = null };
		return capped;
	}
}
