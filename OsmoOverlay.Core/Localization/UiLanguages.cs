using System.Globalization;

namespace OsmoOverlay.Core.Localization;

/// <summary>
///     The interface languages (OverlaySettings.UiLanguage), applied once at startup as the UI culture only - CurrentCulture
///     (number and date formats) stays the system's. English is the neutral resources, Polish a satellite assembly.
/// </summary>
public static class UiLanguages
{
	public const string English = "en";
	public const string Polish = "pl";

	// The user's display language as the process started - Apply replaces CurrentUICulture, and InstalledUICulture is
	// the language Windows was installed in, not the one the user picked.
	private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;

	public static IReadOnlyList<string> Supported { get; } = [English, Polish];

	/// <summary>A supported code as is; null or anything else = the system's UI language if supported, else English.</summary>
	public static string Resolve(string? code)
	{
		if (code is not null && Supported.Contains(code)) return code;
		string system = SystemCulture.TwoLetterISOLanguageName;
		return Supported.Contains(system) ? system : English;
	}

	public static void Apply(string? code)
	{
		var culture = CultureInfo.GetCultureInfo(Resolve(code));
		CultureInfo.DefaultThreadCurrentUICulture = culture;
		CultureInfo.CurrentUICulture = culture;
	}

	/// <summary>The language's own name, as the picker shows it whatever the current language.</summary>
	public static string NativeName(string code)
	{
		return code switch
		{
			Polish => "Polski",
			_ => "English"
		};
	}
}
