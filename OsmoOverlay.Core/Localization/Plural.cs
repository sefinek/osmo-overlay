using System.Globalization;

namespace OsmoOverlay.Core.Localization;

/// <summary>
///     A resource string with plural forms separated by '|', in the language's order: English "one|other",
///     Polish "one|few|many" (1 plik, 2-4 pliki, 5+ i 12-14 plików). {0} is the count, {1}... the extra arguments.
/// </summary>
public static class Plural
{
	public static string Format(string forms, long count, params object?[] args)
	{
		string[] parts = forms.Split('|');
		string form = parts[Math.Min(FormIndex(count, CultureInfo.CurrentUICulture), parts.Length - 1)];
		return string.Format(form, [count, .. args]);
	}

	internal static int FormIndex(long count, CultureInfo culture)
	{
		long n = Math.Abs(count);
		if (culture.TwoLetterISOLanguageName != UiLanguages.Polish) return n == 1 ? 0 : 1;
		if (n == 1) return 0;
		long tens = n % 100;
		return n % 10 is >= 2 and <= 4 && tens is < 12 or > 14 ? 1 : 2;
	}
}
