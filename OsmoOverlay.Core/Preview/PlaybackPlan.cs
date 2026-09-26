namespace OsmoOverlay.Core.Preview;

/// <summary>A span of the recording that plays through without a jump - a kept piece, or the part of one that's played.</summary>
internal sealed record PlaybackStretch(TimeSpan Start, TimeSpan End)
{
	public double Seconds => (End - Start).TotalSeconds;
}

/// <summary>
///     What one Play covers: First (what's left from the starting position) and then - looping - Repeat over and
///     over. The play timeline is these stretches back to back from 0; video and sound both walk it, so cut-out
///     parts are skipped in the picture and in the sound alike, the way the render leaves them out.
/// </summary>
internal sealed record PlaybackPlan(IReadOnlyList<PlaybackStretch> First, IReadOnlyList<PlaybackStretch> Repeat)
{
	/// <summary>
	///     From a position up to the end (of the loop range, or of the recording); with cuts only the kept pieces.
	///     Looping, the same range from its start follows over and over; a position outside the loop range starts at
	///     its start.
	/// </summary>
	public static PlaybackPlan For(OutputTimeline? timeline, TimeSpan from, TimeSpan duration, bool loop, TimeRange? loopRange)
	{
		var start = loop && loopRange is { } range ? Math.Max(0, range.StartSeconds) : 0;
		var end = loop && loopRange is { } limit ? Math.Min(limit.EndSeconds, duration.TotalSeconds) : duration.TotalSeconds;
		var first = from.TotalSeconds;
		if (loop && (first < start || first >= end)) first = start;

		return new PlaybackPlan(Kept(timeline, first, end), loop ? Kept(timeline, start, end) : []);
	}

	public IEnumerable<PlaybackStretch> Stretches()
	{
		foreach (PlaybackStretch stretch in First) yield return stretch;
		if (Repeat.Count == 0) yield break;

		while (true)
			foreach (PlaybackStretch stretch in Repeat)
				yield return stretch;
	}

	/// <summary>The stretches from a point on the play timeline on, the first one cut to start there.</summary>
	public IEnumerable<PlaybackStretch> StretchesFrom(double playSeconds)
	{
		double stretchStart = 0;
		var found = false;
		foreach (PlaybackStretch stretch in Stretches())
		{
			if (found)
			{
				yield return stretch;
				continue;
			}

			var stretchEnd = stretchStart + stretch.Seconds;
			if (playSeconds < stretchEnd)
			{
				found = true;
				yield return stretch with { Start = stretch.Start + TimeSpan.FromSeconds(Math.Max(0, playSeconds - stretchStart)) };
			}

			stretchStart = stretchEnd;
		}
	}

	private static List<PlaybackStretch> Kept(OutputTimeline? timeline, double from, double end)
	{
		if (timeline is null) return end > from ? [new PlaybackStretch(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(end))] : [];

		List<PlaybackStretch> stretches = [];
		var position = from;
		while (timeline.NextKeptStretch(position) is { } kept && kept.Start < end)
		{
			var stretchEnd = Math.Min(kept.End, end);
			if (stretchEnd > kept.Start) stretches.Add(new PlaybackStretch(TimeSpan.FromSeconds(kept.Start), TimeSpan.FromSeconds(stretchEnd)));
			position = kept.End;
		}

		return stretches;
	}
}
