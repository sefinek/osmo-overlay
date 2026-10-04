using Avalonia.Controls;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>A trail widget's route as edited - Color null: the built-in green.</summary>
public sealed record TrailStyle(bool Visible, string? Color, bool BySpeed, float Width);

/// <summary>The route's look on the Map and Compass widgets (TrailOverlayElement) - one control for both.</summary>
public partial class TrailStyleEditor : UserControl
{
	private bool _populating;

	public TrailStyleEditor()
	{
		InitializeComponent();
		ColorBox.PlaceholderText = OverlayRenderer.DefaultTrailColorHex;

		VisibleCheck.Click += (_, _) => OnChanged();
		BySpeedCheck.Click += (_, _) => OnChanged();
		WidthBox.ValueChanged += (_, _) => OnChanged();
		// LostFocus, not TextChanged: a half-typed hex would otherwise re-render the preview per keystroke.
		ColorBox.LostFocus += (_, _) => OnChanged();
	}

	/// <summary>Raised on user edits only, not while Populate fills the controls.</summary>
	public event Action<TrailStyle>? Changed;

	public void Populate(TrailOverlayElement element)
	{
		_populating = true;
		VisibleCheck.IsChecked = element.TrailVisible;
		OptionsPanel.IsEnabled = element.TrailVisible;
		ColorBox.Text = element.TrailColor ?? OverlayRenderer.DefaultTrailColorHex;
		ColorSwatch.Update(SwatchBorder, element.TrailColor, OverlayRenderer.DefaultTrailColorHex);
		BySpeedCheck.IsChecked = element.TrailColorBySpeed;
		WidthBox.Value = (decimal)element.TrailWidth;
		_populating = false;
	}

	private void OnChanged()
	{
		if (_populating) return;

		bool visible = VisibleCheck.IsChecked == true;
		OptionsPanel.IsEnabled = visible;
		string? color = string.IsNullOrWhiteSpace(ColorBox.Text) ? null : ColorBox.Text.Trim();
		ColorSwatch.Update(SwatchBorder, color, OverlayRenderer.DefaultTrailColorHex);
		Changed?.Invoke(new TrailStyle(visible, color, BySpeedCheck.IsChecked == true, WidthBox.Value is { } width ? (float)width : 4.5f));
	}
}
