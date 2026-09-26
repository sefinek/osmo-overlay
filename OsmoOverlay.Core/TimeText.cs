using System.Globalization;

namespace OsmoOverlay.Core;

/// <summary>
///     Times on a recording's timeline as people type and read them - shared by the CLI's --from/--to/--cut,
///     the GUI's cut editor and every log line about a range, so all of them accept and print the same thing.
/// </summary>
public static class TimeText
{
	/// <summary>Seconds ("90", "90.5") or [h:]mm:ss[.fff] ("1:30", "1:02:03.25"); '.' or ',' as the decimal separator.</summary>
	public static bool TryParse(string? text, out double seconds)
	{
		seconds = 0;
		if (string.IsNullOrWhiteSpace(text)) return false;

		var parts = text.Trim().Replace(',', '.').Split(':');
		if (parts.Length > 3) return false;

		for (var i = 0; i < parts.Length; i++)
		{
			if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return false;
			// Only the last part may carry a fraction, and minutes/seconds after the first part stay below 60.
			if (i < parts.Length - 1 && value != Math.Floor(value)) return false;
			if (i > 0 && value >= 60) return false;
			seconds = seconds * 60 + value;
		}

		return true;
	}

	/// <summary>"01:30.250", or "1:02:03.250" from an hour on - millisecond precision, finer than a frame.</summary>
	public static string Format(double seconds)
	{
		TimeSpan time = TimeSpan.FromSeconds(Math.Round(Math.Max(0, seconds), 3));
		return time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss\.fff" : @"mm\:ss\.fff", CultureInfo.InvariantCulture);
	}
}
