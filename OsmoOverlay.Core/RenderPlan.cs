namespace OsmoOverlay.Core;

/// <summary>A stretch of the recording in seconds, e.g. a part to cut out.</summary>
public sealed record TimeRange(double StartSeconds, double EndSeconds);

/// <summary>Frames [SourceStartFrame, SourceStartFrame + FrameCount) of the recording, on the combined timeline of all segments.</summary>
public sealed record RenderPiece(long SourceStartFrame, long FrameCount)
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
	public long TotalFrames => Pieces.Sum(p => p.FrameCount);

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

		var start = startSeconds is { } s ? ToFrame(s) : 0;
		var end = endSeconds is { } e ? ToFrame(e) : sourceFrames;
		if (end <= start)
			throw new InvalidOperationException(
				$"The render range is empty ({TimeText.Format(start / fps)} - {TimeText.Format(end / fps)}) - the end must come after the start.");

		List<RenderPiece> pieces = [];
		var cursor = start;
		foreach (var (cutStart, cutEnd) in (cutOuts ?? [])
		         .Select(c => (Start: ToFrame(c.StartSeconds), End: ToFrame(c.EndSeconds)))
		         .Where(c => c.End > c.Start)
		         .OrderBy(c => c.Start))
		{
			if (cutEnd <= cursor) continue;
			if (cutStart >= end) break;
			if (cutStart > cursor) pieces.Add(new RenderPiece(cursor, cutStart - cursor));
			cursor = Math.Max(cursor, cutEnd);
		}

		if (cursor < end) pieces.Add(new RenderPiece(cursor, end - cursor));
		if (pieces.Count == 0) throw new InvalidOperationException("The cuts remove the whole render range - nothing is left to render.");

		if (frameLimit is > 0 and var limit) pieces = CapFrames(pieces, limit);

		var whole = pieces is [{ SourceStartFrame: 0 } only] && only.FrameCount == sourceFrames;
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

		return capped;
	}
}
