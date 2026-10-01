using Avalonia;
using Avalonia.Media;

namespace OsmoOverlay.Gui;

/// <summary>The App.axaml palette for brushes built in code - same resources the XAML uses, so a color is only ever defined there.</summary>
internal static class Palette
{
	public static IBrush Accent => Brush("AccentBrush");
	public static IBrush Success => Brush("SuccessBrush");
	public static IBrush Warning => Brush("WarningBrush");
	public static IBrush Caution => Brush("CautionBrush");
	public static IBrush Alert => Brush("AlertBrush");
	public static IBrush Danger => Brush("DangerBrush");
	public static IBrush TextMuted => Brush("TextMutedBrush");
	public static IBrush TextPrimary => Brush("TextPrimaryBrush");
	public static IBrush Control => Brush("ControlBrush");
	public static IBrush SurfaceSunken => Brush("SurfaceSunkenBrush");
	public static IBrush Stroke => Brush("StrokeBrush");
	public static IBrush StrokeStrong => Brush("StrokeStrongBrush");
	public static IBrush SubtleFill => Brush("SubtleFillBrush");

	/// <summary>The color `t` (0..1) of the way along `stops`, blended between the two neighbouring ones.</summary>
	public static IBrush Blend(double t, params IBrush[] stops)
	{
		double position = Math.Clamp(t, 0, 1) * (stops.Length - 1);
		int from = Math.Min((int)position, stops.Length - 2);
		Color a = ((ISolidColorBrush)stops[from]).Color;
		Color b = ((ISolidColorBrush)stops[from + 1]).Color;
		double share = position - from;
		return new SolidColorBrush(Color.FromRgb(Mix(a.R, b.R, share), Mix(a.G, b.G, share), Mix(a.B, b.B, share)));
	}

	private static byte Mix(byte from, byte to, double share)
	{
		return (byte)Math.Round(from + (to - from) * share);
	}

	/// <summary>A palette color as a translucent fill, e.g. a status pill's background behind text in the same color.</summary>
	public static IBrush Tint(IBrush brush, double opacity)
	{
		return new SolidColorBrush(((ISolidColorBrush)brush).Color, opacity);
	}

	private static IBrush Brush(string key)
	{
		return Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out object? value) && value is IBrush brush
			? brush
			: throw new InvalidOperationException($"Brush resource '{key}' is missing from App.axaml.");
	}
}
