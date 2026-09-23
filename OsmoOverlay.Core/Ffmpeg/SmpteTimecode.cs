using System.Globalization;

namespace OsmoOverlay.Core.Ffmpeg;

/// <summary>
///     SMPTE timecode arithmetic, so a render that starts partway into a recording carries the timecode
///     of its own first frame (what an NLE lines it up by) rather than the recording's. Handles drop-frame
///     ("HH:MM:SS;FF", which Osmo writes at 59.94/29.97): frame numbers 0..(fps/15 - 1) are skipped at the
///     start of every minute except each tenth one, so the label keeps pace with wall-clock time.
/// </summary>
public static class SmpteTimecode
{
	/// <summary>`timecode` moved forward by `frames`, in the same format; null if it doesn't parse.</summary>
	public static string? AddFrames(string timecode, long frames, double fps)
	{
		if (!TryParse(timecode, out var hh, out var mm, out var ss, out var ff, out var dropFrame)) return null;

		var nominal = (int)Math.Round(fps);
		if (nominal <= 0 || ff >= nominal) return null;
		// Drop-frame only exists for the NTSC rates (29.97 drops 2, 59.94 drops 4).
		var drop = dropFrame ? nominal / 15 : 0;
		if (dropFrame && nominal % 30 != 0) return null;

		var totalMinutes = hh * 60L + mm;
		var frameNumber = (hh * 3600L + mm * 60L + ss) * nominal + ff - drop * (totalMinutes - totalMinutes / 10);

		var framesPerDay = 24L * 3600 * nominal - drop * (24L * 60 - 24L * 6);
		return Format((((frameNumber + frames) % framesPerDay) + framesPerDay) % framesPerDay, nominal, drop, dropFrame);
	}

	private static string Format(long frameNumber, int nominal, int drop, bool dropFrame)
	{
		if (drop > 0)
		{
			var framesPer10Minutes = nominal * 600L - drop * 9L;
			var framesPerMinute = nominal * 60L - drop;
			var tens = frameNumber / framesPer10Minutes;
			var remainder = frameNumber % framesPer10Minutes;
			frameNumber += drop * 9L * tens + (remainder > drop ? drop * ((remainder - drop) / framesPerMinute) : 0);
		}

		var ff = frameNumber % nominal;
		var totalSeconds = frameNumber / nominal;
		return string.Create(CultureInfo.InvariantCulture,
			$"{totalSeconds / 3600 % 24:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}{(dropFrame ? ';' : ':')}{ff:00}");
	}

	private static bool TryParse(string timecode, out int hh, out int mm, out int ss, out int ff, out bool dropFrame)
	{
		hh = mm = ss = ff = 0;
		dropFrame = timecode.Contains(';') || timecode.Contains('.');
		var parts = timecode.Split(':', ';', '.');
		return parts.Length == 4 &&
		       int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hh) &&
		       int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out mm) &&
		       int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ss) &&
		       int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out ff) &&
		       hh < 24 && mm < 60 && ss < 60;
	}
}
