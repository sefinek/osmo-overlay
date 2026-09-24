using Avalonia;
using Avalonia.Media;

namespace OsmoOverlay.Gui;

/// <summary>Icons.axaml geometries for icons set from code - same resources the XAML uses.</summary>
internal static class Icons
{
	public static Geometry Play => Get("IconPlay");
	public static Geometry Pause => Get("IconPause");
	public static Geometry Close => Get("IconClose");
	public static Geometry Check => Get("IconCheck");
	public static Geometry Warning => Get("IconWarning");
	public static Geometry Exclamation => Get("IconExclamation");
	public static Geometry Info => Get("IconInfo");
	public static Geometry Cut => Get("IconCut");
	public static Geometry Volume => Get("IconVolume");
	public static Geometry VolumeMuted => Get("IconVolumeMuted");

	private static Geometry Get(string key)
	{
		return Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is Geometry geometry
			? geometry
			: throw new InvalidOperationException($"Icon '{key}' is missing from Icons.axaml.");
	}
}
