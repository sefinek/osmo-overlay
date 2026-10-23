using System.Globalization;
using System.Text;

namespace OsmoOverlay.Core;

/// <summary>A point of the volume line: a moment of the recording (seconds) and the level there in dB - 0 is the recording's own.</summary>
public readonly record struct VolumePoint(double Seconds, double Db);

/// <summary>A stretch of the output with the level running straight (in dB) from Db0 at T0 to Db1 at T1 - what the render's volume filter is built from.</summary>
public readonly record struct VolumeSegment(double T0, double T1, double Db0, double Db1);

/// <summary>
///     The sound's volume over the recording, as the GUI edits it: points joined by straight lines in dB, before the first
///     and after the last point the level stays as it is there, no points at all is the recording's own sound. Everything
///     works on and returns a normalized list (sorted, inside the recording and the dB range, one point per moment, nothing
///     when every point is at 0 dB). The line is in recording seconds, so it stays with the sound whatever is cut around it.
/// </summary>
public static class VolumeLine
{
	public const double MaxDb = 12;

	/// <summary>The bottom of the range - at it the sound is off.</summary>
	public const double SilentDb = -48;

	/// <summary>Closest two points can be, so a drag can't stack them.</summary>
	public const double MinGapSeconds = 0.02;

	private const double NeutralDb = 0.05;

	public static List<VolumePoint> Normalize(IEnumerable<VolumePoint> points, double durationSeconds)
	{
		List<VolumePoint> normalized = [];
		foreach (VolumePoint point in points
					 .Where(p => double.IsFinite(p.Seconds) && double.IsFinite(p.Db))
					 .Select(p => new VolumePoint(Math.Clamp(p.Seconds, 0, Math.Max(0, durationSeconds)), Math.Clamp(p.Db, SilentDb, MaxDb)))
					 .OrderBy(p => p.Seconds))
		{
			if (normalized.Count > 0 && point.Seconds - normalized[^1].Seconds < MinGapSeconds / 2) normalized[^1] = point;
			else normalized.Add(point);
		}

		return normalized.All(p => Math.Abs(p.Db) < NeutralDb) ? [] : normalized;
	}

	public static double DbAt(IReadOnlyList<VolumePoint> points, double seconds)
	{
		if (points.Count == 0) return 0;
		if (seconds <= points[0].Seconds) return points[0].Db;
		if (seconds >= points[^1].Seconds) return points[^1].Db;

		int right = 1;
		while (points[right].Seconds < seconds) right++;

		VolumePoint a = points[right - 1], b = points[right];
		return a.Db + (b.Db - a.Db) * (seconds - a.Seconds) / (b.Seconds - a.Seconds);
	}

	/// <summary>The factor the samples are multiplied by - 0 at the bottom of the range.</summary>
	public static double Gain(double db)
	{
		return db <= SilentDb ? 0 : Math.Pow(10, db / 20);
	}

	public static double GainAt(IReadOnlyList<VolumePoint> points, double seconds)
	{
		return Gain(DbAt(points, seconds));
	}

	/// <summary>A point on the line where it is at `seconds` - the line itself doesn't change.</summary>
	public static List<VolumePoint> Add(IReadOnlyList<VolumePoint> points, double seconds, double durationSeconds)
	{
		return Normalize([.. points, new VolumePoint(seconds, DbAt(points, seconds))], durationSeconds);
	}

	public static List<VolumePoint> Remove(IReadOnlyList<VolumePoint> points, int index, double durationSeconds)
	{
		List<VolumePoint> rest = [.. points];
		if (index >= 0 && index < rest.Count) rest.RemoveAt(index);
		return Normalize(rest, durationSeconds);
	}

	/// <summary>The index-th point moved, kept between its neighbours.</summary>
	public static List<VolumePoint> Move(IReadOnlyList<VolumePoint> points, int index, double seconds, double db, double durationSeconds)
	{
		if (index < 0 || index >= points.Count) return [.. points];

		double min = index > 0 ? points[index - 1].Seconds + MinGapSeconds : 0;
		double max = index < points.Count - 1 ? points[index + 1].Seconds - MinGapSeconds : Math.Max(0, durationSeconds);
		List<VolumePoint> moved = [.. points];
		moved[index] = new VolumePoint(Math.Clamp(seconds, min, Math.Max(min, max)), Math.Clamp(db, SilentDb, MaxDb));
		return Normalize(moved, durationSeconds);
	}

	/// <summary>
	///     The line at `seconds` raised or lowered by `deltaDb`: the two points either side of it move together (the one that
	///     exists, before the first and after the last), so the stretch between them keeps its slope. Without any point the
	///     whole recording gets one level. The change is cut down so no point leaves the range.
	/// </summary>
	public static List<VolumePoint> Shift(IReadOnlyList<VolumePoint> points, double seconds, double deltaDb, double durationSeconds)
	{
		if (points.Count == 0)
		{
			double flat = Math.Clamp(deltaDb, SilentDb, MaxDb);
			return Normalize([new VolumePoint(0, flat), new VolumePoint(durationSeconds, flat)], durationSeconds);
		}

		int right = 0;
		while (right < points.Count && points[right].Seconds <= seconds) right++;
		int[] moved = right == 0 ? [0] : right == points.Count ? [points.Count - 1] : [right - 1, right];

		double low = SilentDb - moved.Min(i => points[i].Db), high = MaxDb - moved.Max(i => points[i].Db);
		double delta = Math.Clamp(deltaDb, low, high);
		List<VolumePoint> shifted = [.. points];
		foreach (int i in moved) shifted[i] = shifted[i] with { Db = shifted[i].Db + delta };
		return Normalize(shifted, durationSeconds);
	}

	/// <summary>How long the sound comes in from silence at the start: the line's first point at 0 s at the bottom of the range, and the next one where it is back at its level.</summary>
	public static double FadeInSeconds(IReadOnlyList<VolumePoint> points)
	{
		return points.Count >= 2 && points[0].Seconds < MinGapSeconds && points[0].Db <= SilentDb ? points[1].Seconds : 0;
	}

	/// <summary>How long the sound goes out to silence at the end - the way in's mirror.</summary>
	public static double FadeOutSeconds(IReadOnlyList<VolumePoint> points, double durationSeconds)
	{
		return points.Count >= 2 && points[^1].Seconds > durationSeconds - MinGapSeconds && points[^1].Db <= SilentDb ? durationSeconds - points[^2].Seconds : 0;
	}

	/// <summary>The line with its way in set to `seconds` (0 takes it away), starting from silence and ending at the line's own level there.</summary>
	public static List<VolumePoint> WithFadeIn(IReadOnlyList<VolumePoint> points, double seconds, double durationSeconds)
	{
		List<VolumePoint> rest = FadeInSeconds(points) > 0 ? [.. points.Skip(1)] : [.. points];
		if (seconds < MinGapSeconds) return Normalize(rest, durationSeconds);

		double end = Math.Min(seconds, Math.Max(MinGapSeconds, durationSeconds - FadeOutSeconds(points, durationSeconds) - MinGapSeconds));
		return Normalize([new VolumePoint(0, SilentDb), new VolumePoint(end, DbAt(rest, end)), .. rest.Where(p => p.Seconds > end + MinGapSeconds)], durationSeconds);
	}

	/// <summary>The line with its way out set to `seconds` (0 takes it away), from the line's own level to silence at the end.</summary>
	public static List<VolumePoint> WithFadeOut(IReadOnlyList<VolumePoint> points, double seconds, double durationSeconds)
	{
		List<VolumePoint> rest = FadeOutSeconds(points, durationSeconds) > 0 ? [.. points.SkipLast(1)] : [.. points];
		if (seconds < MinGapSeconds) return Normalize(rest, durationSeconds);

		double start = Math.Max(durationSeconds - seconds, FadeInSeconds(points) + MinGapSeconds);
		return Normalize([.. rest.Where(p => p.Seconds < start - MinGapSeconds), new VolumePoint(start, DbAt(rest, start)), new VolumePoint(durationSeconds, SilentDb)], durationSeconds);
	}

	/// <summary>
	///     The line along the output, a segment per stretch between its points and the plan's pieces: every piece starts and
	///     ends a segment, so a cut shows as a step. Where pieces overlap (a crossfade) the later one decides.
	/// </summary>
	public static List<VolumeSegment> ForOutput(IReadOnlyList<VolumePoint> points, RenderPlan plan, double fps)
	{
		List<VolumeSegment> segments = [];
		long outputFrame = 0;
		for (int p = 0; p < plan.Pieces.Count; p++)
		{
			RenderPiece piece = plan.Pieces[p];
			outputFrame -= piece.OverlapIn;
			double start = outputFrame / fps;
			double end = start + piece.FrameCount / fps;
			outputFrame += piece.FrameCount;

			double sourceStart = piece.SourceStartFrame / fps;
			double sourceEnd = piece.SourceEndFrame / fps;
			double? nextStart = p + 1 < plan.Pieces.Count ? (outputFrame - plan.Pieces[p + 1].OverlapIn) / fps : null;
			if (nextStart is { } next) end = Math.Min(end, next);

			List<(double Seconds, double Db)> knots = [(sourceStart, DbAt(points, sourceStart))];
			knots.AddRange(points.Where(k => k.Seconds > sourceStart && k.Seconds < sourceEnd).Select(k => (k.Seconds, k.Db)));
			knots.Add((sourceEnd, DbAt(points, sourceEnd)));
			for (int i = 1; i < knots.Count; i++)
			{
				double t0 = start + (knots[i - 1].Seconds - sourceStart), t1 = Math.Min(start + (knots[i].Seconds - sourceStart), end);
				if (t1 - t0 < 1e-6) continue;

				segments.Add(new VolumeSegment(t0, t1, knots[i - 1].Db, knots[i].Db));
			}
		}

		return segments;
	}

	/// <summary>
	///     The volume filter's expression of the output's time `t` for these segments - straight in dB between their ends, off at the
	///     bottom of the range, and the last segment's level after the end. Numbers are written with the invariant culture.
	/// </summary>
	public static string FfmpegExpression(IReadOnlyList<VolumeSegment> segments)
	{
		static string N(double value)
		{
			return value.ToString("0.######", CultureInfo.InvariantCulture);
		}

		static string Level(VolumeSegment s)
		{
			if (s.Db0 <= SilentDb && s.Db1 <= SilentDb) return "0";
			if (Math.Abs(s.Db1 - s.Db0) < 1e-6) return $"{N(Gain(s.Db0))}";

			return $"pow(10,({N(s.Db0)}+({N(s.Db1 - s.Db0)})*(t-{N(s.T0)})/{N(s.T1 - s.T0)})/20)";
		}

		if (segments.Count == 0) return "1";

		var expression = new StringBuilder();
		for (int i = 0; i < segments.Count - 1; i++) expression.Append($"if(lt(t,{N(segments[i].T1)}),{Level(segments[i])},");
		expression.Append(Level(segments[^1]));
		expression.Append(')', segments.Count - 1);
		return expression.ToString();
	}
}
