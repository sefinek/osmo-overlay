using Avalonia.Controls;
using Avalonia.Media;

namespace OsmoOverlay.Gui;

internal static class ColorSwatch
{
	/// <summary>
	///     Resolves a hand-typed hex string (e.g. "#46DC6E") to a swatch preview color, falling back to
	///     `fallbackHex` when the text is empty or doesn't parse - matches OverlayRenderer.ResolveColor's
	///     fail-soft policy (TrailColor/TextColor/AccentColor/OutlineColor all share it), so what the swatch
	///     shows is exactly what the render will actually use.
	/// </summary>
	public static void Update(Border swatch, string? hex, string fallbackHex)
	{
		Color color = !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex.Trim(), out Color parsed)
			? parsed
			: Color.Parse(fallbackHex);
		swatch.Background = new SolidColorBrush(color);
	}
}
