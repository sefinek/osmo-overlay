using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     The settings every widget (shadow) or every panel widget (panel) shares - ElementShadowEditor and ElementPanelEditor -
///     and their "apply to all" buttons, which hand the edited widget's values to the rest of the preset.
/// </summary>
public partial class MainWindow
{
	private OverlayElement? EditedElement => _editingElementId is { } id ? ActiveElements.FirstOrDefault(e => e.Id == id) : null;

	private void OnElementShadowChanged(ElementShadow shadow)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id) return;

		UpdateElement(id, el => WithShadow(el, shadow));
	}

	private void OnElementPanelChanged(ElementPanel panel)
	{
		if (_suppressOverlayEvents || _editingElementId is not { } id) return;

		UpdateElement(id, el => WithPanel(el, panel));
	}

	private void ApplyShadowToAll()
	{
		if (EditedElement is not { } source) return;

		var shadow = new ElementShadow(source.ShadowEnabled, source.ShadowColor, source.ShadowOpacity, source.ShadowRadius, source.ShadowOffsetX, source.ShadowOffsetY);
		UpdateAllElements("Shadow", el => WithShadow(el, shadow));
	}

	private void ApplyPanelToAll()
	{
		if (EditedElement is not IPanelElement source) return;

		var panel = new ElementPanel(source.PanelColor, source.PanelOpacity);
		UpdateAllElements("Panel", el => WithPanel(el, panel));
	}

	/// <summary>The font, text color and outline - not the size (it's the widget's own) or the value color (only some widgets have one).</summary>
	private void ApplyStyleToAll()
	{
		if (EditedElement is not StyledOverlayElement source) return;

		UpdateAllElements("Font and colors", el => el is StyledOverlayElement styled
			? styled with
			{
				FontFamily = source.FontFamily,
				TextColor = source.TextColor,
				OutlineColor = source.OutlineColor,
				OutlineWidth = source.OutlineWidth
			}
			: el);
	}

	/// <summary>One undo step, and a line in the log saying how many widgets it reached - none when they all had it already.</summary>
	private void UpdateAllElements(string what, Func<OverlayElement, OverlayElement> update)
	{
		if (_suppressOverlayEvents || IsActivePresetDefault) return;

		List<OverlayElement> elements = [.. ActiveElements];
		int changed = 0;
		for (int i = 0; i < elements.Count; i++)
		{
			OverlayElement updated = update(elements[i]);
			if (updated == elements[i]) continue;

			elements[i] = updated;
			changed++;
		}

		if (changed == 0)
		{
			AppendLog($"{what}: every widget already has it.");
			return;
		}

		ReplaceActiveElements(elements);
		ShowLayout();
		SaveOverlayPresets();
		AppendLog($"{what} applied to {changed} widgets.");
	}

	private static OverlayElement WithShadow(OverlayElement element, ElementShadow shadow)
	{
		return element with
		{
			ShadowEnabled = shadow.Enabled,
			ShadowColor = shadow.Color,
			ShadowOpacity = shadow.Opacity,
			ShadowRadius = shadow.Radius,
			ShadowOffsetX = shadow.OffsetX,
			ShadowOffsetY = shadow.OffsetY
		};
	}

	/// <summary>IPanelElement is an interface, so the widgets that have a panel are listed - a record can't be copied through it.</summary>
	private static OverlayElement WithPanel(OverlayElement element, ElementPanel panel)
	{
		return element switch
		{
			SunWidgetElement sun => sun with { PanelColor = panel.Color, PanelOpacity = panel.Opacity },
			RollGaugeElement roll => roll with { PanelColor = panel.Color, PanelOpacity = panel.Opacity },
			PitchGaugeElement pitch => pitch with { PanelColor = panel.Color, PanelOpacity = panel.Opacity },
			GMeterElement gMeter => gMeter with { PanelColor = panel.Color, PanelOpacity = panel.Opacity },
			SpeedGaugeElement speed => speed with { PanelColor = panel.Color, PanelOpacity = panel.Opacity },
			CompassElement compass => compass with { PanelColor = panel.Color, PanelOpacity = panel.Opacity },
			TripProgressBarElement bar => bar with { PanelColor = panel.Color, PanelOpacity = panel.Opacity },
			ProfileChartElement chart => chart with { PanelColor = panel.Color, PanelOpacity = panel.Opacity },
			_ => element
		};
	}
}
