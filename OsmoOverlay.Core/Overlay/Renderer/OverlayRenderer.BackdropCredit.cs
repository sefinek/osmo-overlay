using SkiaSharp;

namespace OsmoOverlay.Core.Overlay;

/// <summary>The map the picture itself is (TrackBackdrop) - what its credit is written for.</summary>
public sealed record BackdropMap(string? ProviderId, bool ShowLabels);

public sealed partial class OverlayRenderer
{
	/// <summary>The map under the whole picture (a GPS track with no recording). Its credit is drawn here, not in the picture.</summary>
	public BackdropMap? Backdrop { get; set; }

	/// <summary>
	///     The backdrop map's credit, in the credit widget's own look (the layout's, else the default one) at the bottom right - the
	///     Map widgets' credit keeps its place. Null when there is no backdrop, nothing to credit, or the credit of the maps on screen
	///     (`widgetCredit`) already says it: the same map is credited once. The backdrop is always on screen, so it is always drawn.
	/// </summary>
	private (MapAttributionElement Element, string Text)? BackdropCredit(string? widgetCredit)
	{
		if (Backdrop is not { } map || MapSources.Attribution(map.ProviderId, showLabels: map.ShowLabels) is not { } text) return null;
		if (widgetCredit is not null && widgetCredit.Contains(text, StringComparison.Ordinal)) return null;

		MapAttributionElement style = Layout.OfType<MapAttributionElement>().FirstOrDefault() ?? DefaultMapCredit;
		SKRect box = OverlayElementBounds.GetBounds(OverlayElementType.MapAttribution, 0, 0, 1, fontFamily: style.FontFamily, text: text, element: style);
		return (style with { X = OverlayElementBounds.ReferenceWidth - DefaultMapCredit.X - box.Right * style.Scale, Y = DefaultMapCredit.Y, Visible = true }, text);
	}
}
