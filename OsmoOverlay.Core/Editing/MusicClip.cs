using System.Globalization;
using System.Text;

namespace OsmoOverlay.Core;

/// <summary>
///     A music file on the timeline. StartSeconds and LengthSeconds are on the output (after cuts), so a cut doesn't take
///     music out. InSeconds is where in the file it starts, SourceSeconds the file's own length (0 = unknown, then it can't loop).
///     Db is the clip's level; DuckDb (0 or less) is how much the recording's own sound is lowered while the clip plays.
/// </summary>
public sealed record MusicClip(
	string Path,
	double StartSeconds,
	double InSeconds,
	double LengthSeconds,
	double SourceSeconds,
	double Db = 0,
	double FadeInSeconds = 0,
	double FadeOutSeconds = 0,
	bool Loop = false,
	double DuckDb = 0)
{
	public double EndSeconds => StartSeconds + LengthSeconds;

	/// <summary>True when the clip plays past the end of its file and so repeats it.</summary>
	public bool Repeats => Loop && SourceSeconds > 0 && LengthSeconds > SourceSeconds - InSeconds + MusicTrack.MinLengthSeconds / 2;
}

/// <summary>The music track as the GUI edits it and the render and the preview play it: clips mixed over the recording's sound.</summary>
public static class MusicTrack
{
	public const double MinLengthSeconds = 0.1;
	public const double MaxDb = 12;
	public const double SilentDb = VolumeLine.SilentDb;

	/// <summary>The sound's rate and layout in the mix - what every clip and the recording's sound are converted to.</summary>
	public const int MixSampleRate = 48000;

	/// <summary>How long the recording's own sound takes to go down to a clip's DuckDb, and back up after it.</summary>
	public const double DuckRampSeconds = 0.4;

	/// <summary>
	///     Sorted, valid clips inside the output: a start not before 0, a length that fits the file (unless it loops) and the
	///     output, level and ducking in range, the two fades not longer than the clip together. Clips may overlap - they mix.
	///     Without `sort` the order is kept (a clip being edited keeps its index).
	/// </summary>
	public static List<MusicClip> Normalize(IEnumerable<MusicClip> clips, double outputSeconds, bool sort = true)
	{
		List<MusicClip> normalized = [];
		foreach (MusicClip clip in clips)
		{
			if (string.IsNullOrWhiteSpace(clip.Path)
			    || !double.IsFinite(clip.StartSeconds) || !double.IsFinite(clip.InSeconds) || !double.IsFinite(clip.LengthSeconds)
			    || !double.IsFinite(clip.SourceSeconds) || !double.IsFinite(clip.Db) || !double.IsFinite(clip.DuckDb)
			    || !double.IsFinite(clip.FadeInSeconds) || !double.IsFinite(clip.FadeOutSeconds))
				continue;

			double source = Math.Max(0, clip.SourceSeconds);
			double start = Math.Max(0, clip.StartSeconds);
			double inPoint = Math.Clamp(clip.InSeconds, 0, source > 0 ? Math.Max(0, source - MinLengthSeconds) : double.MaxValue);
			double length = Math.Min(clip.LengthSeconds, outputSeconds - start);
			bool loop = clip.Loop && source > 0;
			if (!loop && source > 0) length = Math.Min(length, source - inPoint);
			if (length < MinLengthSeconds) continue;

			double fadeIn = Math.Clamp(clip.FadeInSeconds, 0, length);
			double fadeOut = Math.Clamp(clip.FadeOutSeconds, 0, length - fadeIn);
			normalized.Add(new MusicClip(clip.Path, start, inPoint, length, source, Math.Clamp(clip.Db, SilentDb, MaxDb), fadeIn, fadeOut, loop,
				Math.Clamp(clip.DuckDb, SilentDb, 0)));
		}

		return sort ? [.. normalized.OrderBy(c => c.StartSeconds).ThenBy(c => c.Path, StringComparer.Ordinal)] : normalized;
	}

	/// <summary>
	///     The clips as they are heard where they overlap like a crossfade (as in a video editor): a clip starting over the end of an
	///     earlier one fades in for the length of the overlap, and that one fades out over it - never less than the fade each has by
	///     itself. A clip lying wholly inside another is only mixed with it. Same order as the input; the clips themselves are
	///     what is stored, this is what is played (FfmpegGraph, MusicMixer) and drawn.
	/// </summary>
	public static List<MusicClip> WithCrossfades(IReadOnlyList<MusicClip> clips)
	{
		const double Tolerance = 1e-6;
		double[] fadeIn = [.. clips.Select(c => c.FadeInSeconds)], fadeOut = [.. clips.Select(c => c.FadeOutSeconds)];
		for (int later = 0; later < clips.Count; later++)
		{
			for (int earlier = 0; earlier < clips.Count; earlier++)
			{
				MusicClip a = clips[earlier], b = clips[later];
				if (earlier == later || b.StartSeconds <= a.StartSeconds + Tolerance || b.StartSeconds >= a.EndSeconds - Tolerance || b.EndSeconds <= a.EndSeconds + Tolerance)
					continue;

				double overlap = a.EndSeconds - b.StartSeconds;
				fadeOut[earlier] = Math.Max(fadeOut[earlier], overlap);
				fadeIn[later] = Math.Max(fadeIn[later], overlap);
			}
		}

		return [.. clips.Select((c, i) =>
		{
			double inFade = Math.Min(fadeIn[i], c.LengthSeconds);
			return c with { FadeInSeconds = inFade, FadeOutSeconds = Math.Min(fadeOut[i], c.LengthSeconds - inFade) };
		})];
	}

	/// <summary>The clip's gain at `seconds` into it (fades included), 0 outside the clip.</summary>
	public static double GainAt(MusicClip clip, double seconds)
	{
		return VolumeLine.Gain(clip.Db) * FadeAt(clip, seconds);
	}

	/// <summary>The fades' factor (0-1) at `seconds` into the clip, 0 outside it.</summary>
	public static double FadeAt(MusicClip clip, double seconds)
	{
		if (seconds < 0 || seconds >= clip.LengthSeconds) return 0;

		double fade = 1;
		if (clip.FadeInSeconds > 0 && seconds < clip.FadeInSeconds) fade *= seconds / clip.FadeInSeconds;
		double toEnd = clip.LengthSeconds - seconds;
		if (clip.FadeOutSeconds > 0 && toEnd < clip.FadeOutSeconds) fade *= toEnd / clip.FadeOutSeconds;
		return fade;
	}

	/// <summary>Where in the file the sound is `seconds` into the clip - it wraps round at the file's end when the clip repeats.</summary>
	public static double FilePosition(MusicClip clip, double seconds)
	{
		double span = clip.SourceSeconds - clip.InSeconds;
		return clip.Repeats && span > 0 ? clip.InSeconds + seconds % span : clip.InSeconds + seconds;
	}

	/// <summary>How many dB the recording's own sound is lowered by at `seconds` of the output: each clip's DuckDb, ramped in and out, summed.</summary>
	public static double DuckDbAt(IReadOnlyList<MusicClip> clips, double seconds)
	{
		double db = 0;
		foreach (MusicClip clip in clips)
			db += clip.DuckDb * DuckWeight(clip, seconds);

		return db;
	}

	private static double DuckWeight(MusicClip clip, double seconds)
	{
		if (clip.DuckDb >= 0) return 0;

		double ramp = Math.Min(DuckRampSeconds, clip.LengthSeconds / 2);
		return Math.Clamp(Math.Min((seconds - clip.StartSeconds) / ramp, (clip.EndSeconds - seconds) / ramp), 0, 1);
	}

	/// <summary>
	///     The filter graph that puts the clips (ffmpeg inputs firstInput, firstInput + 1, ...) over the recording's sound
	///     (`original`, a stream label such as "0:a" or "[cuta]", null for a recording without sound) and ends in [mus].
	///     The recording's sound is lowered where a clip asks for it, the sum is kept from clipping.
	/// </summary>
	public static string FfmpegGraph(IReadOnlyList<MusicClip> clips, int firstInput, string? original)
	{
		var graph = new StringBuilder();
		List<string> mixed = [];
		const string Format = "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo";

		if (original is not null)
		{
			graph.Append($"{Bracket(original)}{Format}");
			if (clips.Any(c => c.DuckDb < 0)) graph.Append($",volume=volume='{DuckExpression(clips)}':eval=frame:precision=float");
			graph.Append("[orig];");
			mixed.Add("[orig]");
		}

		for (int i = 0; i < clips.Count; i++)
		{
			MusicClip clip = clips[i];
			graph.Append($"[{firstInput + i}:a]aresample={MixSampleRate},{Format},");
			if (clip.InSeconds > 0) graph.Append($"atrim=start={N(clip.InSeconds)},asetpts=PTS-STARTPTS,");
			if (clip.Repeats)
				graph.Append($"aloop=loop=-1:size={(long)Math.Round((clip.SourceSeconds - clip.InSeconds) * MixSampleRate)}:start=0,");

			graph.Append($"atrim=end={N(clip.LengthSeconds)},asetpts=PTS-STARTPTS");
			if (Math.Abs(clip.Db) >= 0.05) graph.Append($",volume={(clip.Db <= SilentDb ? "0" : N(clip.Db) + "dB")}");
			if (clip.FadeInSeconds > 0) graph.Append($",afade=t=in:st=0:d={N(clip.FadeInSeconds)}");
			if (clip.FadeOutSeconds > 0) graph.Append($",afade=t=out:st={N(clip.LengthSeconds - clip.FadeOutSeconds)}:d={N(clip.FadeOutSeconds)}");
			long delayMs = (long)Math.Round(clip.StartSeconds * 1000);
			if (delayMs > 0) graph.Append($",adelay={delayMs.ToString(CultureInfo.InvariantCulture)}:all=1");
			graph.Append($"[m{i}];");
			mixed.Add($"[m{i}]");
		}

		graph.Append(string.Concat(mixed));
		graph.Append(mixed.Count == 1
			? "anull"
			: $"amix=inputs={mixed.Count}:duration=longest:normalize=0:dropout_transition=0");
		graph.Append(",alimiter=limit=0.98:level=disabled[mus]");
		return graph.ToString();
	}

	/// <summary>The volume filter's gain for the recording's sound along the output: the product of every clip's lowering.</summary>
	public static string DuckExpression(IReadOnlyList<MusicClip> clips)
	{
		IEnumerable<string> terms = clips.Where(c => c.DuckDb < 0).Select(c =>
		{
			string ramp = N(Math.Min(DuckRampSeconds, c.LengthSeconds / 2));
			return $"{N(c.DuckDb)}*clip(min((t-{N(c.StartSeconds)})/{ramp},({N(c.EndSeconds)}-t)/{ramp}),0,1)";
		});
		return $"pow(10,({string.Join('+', terms)})/20)";
	}

	private static string Bracket(string label)
	{
		return label.StartsWith('[') ? label : $"[{label}]";
	}

	private static string N(double value)
	{
		return value.ToString("0.######", CultureInfo.InvariantCulture);
	}
}
