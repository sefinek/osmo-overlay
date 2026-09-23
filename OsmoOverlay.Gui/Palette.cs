using Avalonia;
using Avalonia.Media;

namespace OsmoOverlay.Gui;

/// <summary>The App.axaml palette for brushes built in code - same resources the XAML uses, so a color is only ever defined there.</summary>
internal static class Palette
{
	public static IBrush Accent => Brush("AccentBrush");
	public static IBrush Success => Brush("SuccessBrush");
	public static IBrush Warning => Brush("WarningBrush");
	public static IBrush Danger => Brush("DangerBrush");
	public static IBrush TextMuted => Brush("TextMutedBrush");
	public static IBrush Stroke => Brush("StrokeBrush");
	public static IBrush StrokeStrong => Brush("StrokeStrongBrush");
	public static IBrush SubtleFill => Brush("SubtleFillBrush");

	/// <summary>A palette color as a translucent fill, e.g. a status pill's background behind text in the same color.</summary>
	public static IBrush Tint(IBrush brush, double opacity)
	{
		return new SolidColorBrush(((ISolidColorBrush)brush).Color, opacity);
	}

	private static IBrush Brush(string key)
	{
		return Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush
			? brush
			: throw new InvalidOperationException($"Brush resource '{key}' is missing from App.axaml.");
	}
}
