using System.Globalization;

namespace OsmoOverlay.Core;

/// <summary>
///     The sound level line as the command line writes it: "<c>time:dB,time:dB,...</c>" - times in the CLI's usual forms
///     (seconds or [h:]mm:ss) on the recording, levels in dB or <c>off</c> for the bottom of the range ("0:off,2:0" fades in).
/// </summary>
public static class VolumeSpec
{
	public static bool TryParse(string spec, out List<VolumePoint> points, out string error)
	{
		points = [];
		error = "";
		foreach (string part in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
		{
			int colon = part.LastIndexOf(':');
			if (colon <= 0 || !TimeText.TryParse(part[..colon], out double seconds) || !TryParseDb(part[(colon + 1)..], out double db))
			{
				error = $"'{part}' is not <time>:<dB>";
				return false;
			}

			points.Add(new VolumePoint(seconds, db));
		}

		if (points.Count == 0)
		{
			error = "no points";
			return false;
		}

		points = VolumeLine.Normalize(points, double.MaxValue);
		return true;
	}

	public static string Format(IEnumerable<VolumePoint> points)
	{
		return string.Join(',', points.Select(p => $"{p.Seconds.ToString("0.###", CultureInfo.InvariantCulture)}:{FormatDb(p.Db)}"));
	}

	private static string FormatDb(double db)
	{
		return db <= VolumeLine.SilentDb ? "off" : db.ToString("0.##", CultureInfo.InvariantCulture);
	}

	private static bool TryParseDb(string text, out double db)
	{
		if (text.Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			db = VolumeLine.SilentDb;
			return true;
		}

		return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out db) && double.IsFinite(db);
	}
}

/// <summary>
///     A music clip as the command line writes it: "<c>file[;start=10][;in=5][;len=60][;db=-6][;fadein=2][;fadeout=3][;duck=-12][;loop]</c>".
///     Times are on the output (after cuts), as on the timeline; the file's own length comes from the caller (it's probed).
///     Without <c>len</c> the clip plays to the end of its file (or, with <c>loop</c>, needs a <c>len</c>).
/// </summary>
public static class MusicSpec
{
	public static bool TryParse(string spec, double sourceSeconds, out MusicClip? clip, out string error)
	{
		clip = null;
		error = "";
		string[] parts = spec.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0)
		{
			error = "no file";
			return false;
		}

		double start = 0, inPoint = 0, length = 0, db = 0, fadeIn = 0, fadeOut = 0, duck = 0;
		bool loop = false;
		foreach (string option in parts[1..])
		{
			string[] pair = option.Split('=', 2, StringSplitOptions.TrimEntries);
			if (pair is [var flag] && flag.Equals("loop", StringComparison.OrdinalIgnoreCase))
			{
				loop = true;
				continue;
			}

			bool ok = pair[0].ToLowerInvariant() switch
			{
				"start" => Seconds(pair, out start),
				"in" => Seconds(pair, out inPoint),
				"len" => Seconds(pair, out length) && length > 0,
				"db" => Number(pair, out db),
				"fadein" => Seconds(pair, out fadeIn),
				"fadeout" => Seconds(pair, out fadeOut),
				"duck" => Number(pair, out duck),
				_ => false
			};
			if (!ok)
			{
				error = $"invalid option '{option}'";
				return false;
			}
		}

		if (length <= 0)
		{
			double rest = sourceSeconds - inPoint;
			if (loop || rest <= MusicTrack.MinLengthSeconds)
			{
				error = loop ? "loop needs len" : "the file has nothing after 'in'";
				return false;
			}

			length = rest;
		}

		clip = new MusicClip(parts[0], start, inPoint, length, sourceSeconds, db, fadeIn, fadeOut, loop, Math.Min(0, duck));
		return true;
	}

	public static string Format(MusicClip clip)
	{
		var parts = new List<string> { clip.Path };
		AddIf(parts, "start", clip.StartSeconds);
		AddIf(parts, "in", clip.InSeconds);
		parts.Add($"len={Invariant(clip.LengthSeconds)}");
		AddIf(parts, "db", clip.Db);
		AddIf(parts, "fadein", clip.FadeInSeconds);
		AddIf(parts, "fadeout", clip.FadeOutSeconds);
		AddIf(parts, "duck", clip.DuckDb);
		if (clip.Loop) parts.Add("loop");
		return string.Join(';', parts);
	}

	private static void AddIf(List<string> parts, string name, double value)
	{
		if (value != 0) parts.Add($"{name}={Invariant(value)}");
	}

	private static string Invariant(double value)
	{
		return value.ToString("0.###", CultureInfo.InvariantCulture);
	}

	private static bool Seconds(string[] pair, out double value)
	{
		value = 0;
		return pair.Length == 2 && TimeText.TryParse(pair[1], out value) && value >= 0;
	}

	private static bool Number(string[] pair, out double value)
	{
		value = 0;
		return pair.Length == 2 && double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
	}
}
