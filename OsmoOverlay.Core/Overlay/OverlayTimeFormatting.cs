using System.Globalization;

namespace OsmoOverlay.Core.Overlay;

/// <summary>
///     Date/time and elapsed-time formatting shared between OverlayRenderer (drawing the real value each
///     frame) and OverlayElementBounds (measuring a representative sample of the same shape/format for the
///     GUI's hit-test/selection box) - a single source so the box can't drift from what actually gets drawn.
/// </summary>
internal static class OverlayTimeFormatting
{
	private const string DefaultDateFormat = "dd/MM/yyyy  HH:mm:ss";

	/// <summary>
	///     Formats `shown` with `format`/`locale`, falling back to DefaultDateFormat/CurrentCulture on an
	///     invalid hand-edited preset - `text` is always a usable string either way. Returns false when the
	///     fallback had to be used, so a caller that cares (OverlayRenderer.DrawTimeText) can log it.
	/// </summary>
	public static bool TryFormat(DateTime shown, string? format, string? locale, out string text)
	{
		try
		{
			CultureInfo culture = locale is null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(locale);
			text = shown.ToString(format ?? DefaultDateFormat, culture);
			return true;
		}
		catch (Exception ex) when (ex is CultureNotFoundException or FormatException)
		{
			text = shown.ToString(DefaultDateFormat, CultureInfo.CurrentCulture);
			return false;
		}
	}

	/// <summary>Same shape DrawElapsedTime renders: "H:MM:SS" once the recording passes an hour, "MM:SS" before that.</summary>
	public static string FormatElapsed(double seconds)
	{
		TimeSpan elapsed = TimeSpan.FromSeconds(Math.Max(seconds, 0));
		return elapsed.TotalHours >= 1
			? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
			: $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
	}
}
