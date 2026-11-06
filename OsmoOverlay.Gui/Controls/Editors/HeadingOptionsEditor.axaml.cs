using Avalonia.Controls;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>How a trail widget's direction of travel is shown, as edited (TrailOverlayElement).</summary>
public sealed record HeadingOptions(bool Stabilize, bool DotWhenNotFacing, double ToleranceDegrees);

/// <summary>The steadied direction and the dot for a drone that isn't flying forward - one control for the Map and the Compass.</summary>
public partial class HeadingOptionsEditor : UserControl
{
	private bool _populating;

	public HeadingOptionsEditor()
	{
		InitializeComponent();
		ToleranceBox.Minimum = (decimal)OverlayRenderer.DroneFacingToleranceMin;
		ToleranceBox.Maximum = (decimal)OverlayRenderer.DroneFacingToleranceMax;

		StabilizeCheck.Click += (_, _) => OnChanged();
		DotCheck.Click += (_, _) => OnChanged();
		ToleranceBox.ValueChanged += (_, _) => OnChanged();
	}

	/// <summary>Raised on user edits only, not while Populate fills the controls.</summary>
	public event Action<HeadingOptions>? Changed;

	public void Populate(TrailOverlayElement element)
	{
		_populating = true;
		StabilizeCheck.IsChecked = element.StabilizeHeading;
		DotCheck.IsChecked = element.DotWhenNotFacingTravel;
		ToleranceBox.Value = (decimal)element.DroneFacingToleranceDegrees;
		ShowTolerance(element.DotWhenNotFacingTravel);
		_populating = false;
	}

	private void ShowTolerance(bool visible)
	{
		ToleranceLabel.IsVisible = ToleranceBox.IsVisible = visible;
	}

	private void OnChanged()
	{
		if (_populating) return;

		bool dot = DotCheck.IsChecked == true;
		ShowTolerance(dot);
		Changed?.Invoke(new HeadingOptions(StabilizeCheck.IsChecked == true, dot, ToleranceBox.Value is { } degrees ? (double)degrees : OverlayRenderer.DroneFacingToleranceDefault));
	}
}
