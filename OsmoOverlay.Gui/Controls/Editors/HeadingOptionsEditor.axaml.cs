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

	/// <summary>The dot option is offered only when the loaded data has a drone's yaw (a flight log) - for a camera it could never fire.</summary>
	public void Populate(TrailOverlayElement element, bool droneData)
	{
		_populating = true;
		_droneData = droneData;
		StabilizeCheck.IsChecked = element.StabilizeHeading;
		DotCheck.IsChecked = element.DotWhenNotFacingTravel;
		DotCheck.IsVisible = DotHint.IsVisible = droneData;
		ToleranceBox.Value = (decimal)element.DroneFacingToleranceDegrees;
		ShowTolerance(element.DotWhenNotFacingTravel);
		_populating = false;
	}

	private bool _droneData;

	private void ShowTolerance(bool visible)
	{
		ToleranceLabel.IsVisible = ToleranceBox.IsVisible = visible && _droneData;
	}

	private void OnChanged()
	{
		if (_populating) return;

		bool dot = DotCheck.IsChecked == true;
		ShowTolerance(dot);
		Changed?.Invoke(new HeadingOptions(StabilizeCheck.IsChecked == true, dot, ToleranceBox.Value is { } degrees ? (double)degrees : OverlayRenderer.DroneFacingToleranceDefault));
	}
}
