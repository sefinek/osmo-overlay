using Avalonia.Controls;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>A trail widget's position marker as edited - Color null: the accent color.</summary>
public sealed record MarkerStyle(bool UseArrow, float Scale, string? Color);

/// <summary>The arrow or dot on the Map and Compass widgets (TrailOverlayElement) - one control for both.</summary>
public partial class MarkerStyleEditor : UserControl
{
	private bool _populating;

	public MarkerStyleEditor()
	{
		InitializeComponent();
		// Radio buttons group by name across the whole window, and the Map and Compass editors are both in it.
		string group = Guid.NewGuid().ToString("N");
		ArrowRadio.GroupName = group;
		DotRadio.GroupName = group;
		ColorBox.PlaceholderText = OverlayRenderer.DefaultAccentColorHex;

		ArrowRadio.Click += (_, _) => OnChanged();
		DotRadio.Click += (_, _) => OnChanged();
		ScaleBox.ValueChanged += (_, _) => OnChanged();
		// LostFocus, not TextChanged: a half-typed hex would otherwise re-render the preview per keystroke.
		ColorBox.LostFocus += (_, _) => OnChanged();
	}

	/// <summary>Raised on user edits only, not while Populate fills the controls.</summary>
	public event Action<MarkerStyle>? Changed;

	public void Populate(TrailOverlayElement element)
	{
		_populating = true;
		ArrowRadio.IsChecked = element.TrailUseArrow;
		DotRadio.IsChecked = !element.TrailUseArrow;
		ScaleBox.Value = (decimal)element.MarkerScale;
		ColorBox.Text = element.MarkerColor ?? OverlayRenderer.DefaultAccentColorHex;
		ColorSwatch.Update(SwatchBorder, element.MarkerColor, OverlayRenderer.DefaultAccentColorHex);
		_populating = false;
	}

	private void OnChanged()
	{
		if (_populating) return;

		string? color = string.IsNullOrWhiteSpace(ColorBox.Text) ? null : ColorBox.Text.Trim();
		ColorSwatch.Update(SwatchBorder, color, OverlayRenderer.DefaultAccentColorHex);
		Changed?.Invoke(new MarkerStyle(ArrowRadio.IsChecked == true, ScaleBox.Value is { } scale ? (float)scale : 1f, color));
	}
}
