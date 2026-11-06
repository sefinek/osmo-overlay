using Avalonia.Controls;
using Telemavix.Core.Overlay;

namespace Telemavix.Gui;

/// <summary>A trail widget's route as edited - Color null: the built-in green.</summary>
public sealed record TrailStyle(bool Visible, string? Color, bool BySpeed, float Width, RouteJoin GpsLossJoin);

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
		GpsLossCombo.ItemsSource = RouteJoinOptions.Labels;
		GpsLossCombo.SelectionChanged += (_, _) => OnChanged();
		// LostFocus, not TextChanged: a half-typed hex would otherwise re-render the preview per keystroke.
		ColorBox.LostFocus += (_, _) => OnChanged();
	}

	/// <summary>Raised on user edits only, not while Populate fills the controls.</summary>
	public event Action<TrailStyle>? Changed;

	/// <summary>False where the route has no join to choose (a map drawn from the track alone breaks the line at a gap).</summary>
	public bool GpsLossVisible
	{
		get => GpsLossPanel.IsVisible;
		set => GpsLossPanel.IsVisible = value;
	}

	public void Populate(TrailOverlayElement element)
	{
		Populate(element.TrailVisible, element.TrailColor, element.TrailColorBySpeed, element.TrailWidth, element.GpsLossJoin);
	}

	public void Populate(bool visible, string? color, bool bySpeed, float width, RouteJoin? gpsLossJoin = null)
	{
		_populating = true;
		VisibleCheck.IsChecked = visible;
		OptionsPanel.IsEnabled = visible;
		ColorBox.Text = color ?? OverlayRenderer.DefaultTrailColorHex;
		ColorSwatch.Update(SwatchBorder, color, OverlayRenderer.DefaultTrailColorHex);
		BySpeedCheck.IsChecked = bySpeed;
		WidthBox.Value = (decimal)width;
		if (gpsLossJoin is { } join) GpsLossCombo.SelectedIndex = RouteJoinOptions.IndexOf(join);
		_populating = false;
	}

	private void OnChanged()
	{
		if (_populating) return;

		bool visible = VisibleCheck.IsChecked == true;
		OptionsPanel.IsEnabled = visible;
		string? color = string.IsNullOrWhiteSpace(ColorBox.Text) ? null : ColorBox.Text.Trim();
		ColorSwatch.Update(SwatchBorder, color, OverlayRenderer.DefaultTrailColorHex);
		Changed?.Invoke(new TrailStyle(visible, color, BySpeedCheck.IsChecked == true, WidthBox.Value is { } width ? (float)width : 4.5f,
			RouteJoinOptions.At(GpsLossCombo.SelectedIndex) ?? RouteJoins.Default));
	}
}
