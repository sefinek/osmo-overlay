using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace OsmoOverlay.Gui;

/// <summary>
///     The color themes (Resources/Themes, one file each, the same keys in every one; Dark is App.axaml's default). A theme
///     goes over the app's resources and recolors App.axaml's brushes in place, so everything drawn with a brush follows
///     at once; a color a window took straight from a Color key follows the next time that window opens.
/// </summary>
internal static class AppThemes
{
	public const string Default = "Dark";

	public static IReadOnlyList<ChoiceOption<string>> Options { get; } =
	[
		new("Dark (default)", "Dark"),
		new("Blue", "Blue"),
		new("AMOLED black", "Amoled")
	];

	public static string Normalize(string? theme)
	{
		return Options.FirstOrDefault(o => string.Equals(o.Value, theme, StringComparison.OrdinalIgnoreCase))?.Value ?? Default;
	}

	public static void Apply(string? theme)
	{
		if (Application.Current is not { } app) return;

		var colors = (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri($"avares://OsmoOverlay/Resources/Themes/{Normalize(theme)}.axaml"));
		foreach (KeyValuePair<object, object?> entry in colors)
		{
			if (entry is not { Key: string key, Value: Color color }) continue;

			app.Resources[key] = color;
			if (key.EndsWith("Color", StringComparison.Ordinal) &&
			    app.TryGetResource(key[..^"Color".Length] + "Brush", null, out object? brush) && brush is SolidColorBrush solid)
				solid.Color = color;
		}

		if (app.TryGetResource("AccentGradientBrush", null, out object? gradient) && gradient is LinearGradientBrush { GradientStops.Count: 2 } accent)
		{
			accent.GradientStops[0].Color = (Color)app.Resources["AccentColor"]!;
			accent.GradientStops[1].Color = (Color)app.Resources["AccentGlowColor"]!;
		}
	}
}
