using SkiaSharp;

namespace Telemavix.Core.Overlay;

/// <summary>The map the picture itself is (TrackBackdrop) - what its credit is written for.</summary>
public sealed record BackdropMap(string? ProviderId, bool ShowLabels);

public sealed partial class OverlayRenderer
{
	/// <summary>The map under the whole picture (a GPS track with no recording). Its credit is drawn here, not in the picture.</summary>
	public BackdropMap? Backdrop { get; set; }

	/// <summary>
	///     The backdrop map's credit, drawn by the credit widget with OverlayPreset.BackdropCreditId (its place, scale and look - the other
	///     credit widget keeps crediting the Map widgets); a layout without it draws it in the first one's look at the bottom right. Null when there is no
	///     backdrop, nothing to credit, or the credit of the maps on screen (`widgetCredit`) already says it: the same map is credited
	///     once. The backdrop is always on screen, so it is always drawn, whatever its widget's visibility says.
	/// </summary>
	private (MapAttributionElement Element, string Text)? BackdropCredit(string? widgetCredit)
	{
		if (Backdrop is not { } map || MapSources.Attribution(map.ProviderId, showLabels: map.ShowLabels) is not { } text) return null;
		if (widgetCredit is not null && widgetCredit.Contains(text, StringComparison.Ordinal)) return null;

		MapAttributionElement? own = null, first = null;
		foreach (MapAttributionElement widget in Layout.OfType<MapAttributionElement>())
		{
			if (widget.Id == OverlayPreset.BackdropCreditId) own ??= widget;
			else first ??= widget;
		}

		if (own is not null) return (own with { Visible = true }, text);

		MapAttributionElement style = first ?? DefaultMapCredit;
		SKRect box = OverlayElementBounds.GetBounds(OverlayElementType.MapAttribution, 0, 0, 1, fontFamily: style.FontFamily, text: text, element: style);
		return (style with { X = OverlayElementBounds.ReferenceWidth - DefaultMapCredit.X - box.Right * style.Scale, Y = DefaultMapCredit.Y, Visible = true }, text);
	}
}
