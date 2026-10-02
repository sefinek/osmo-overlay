using Avalonia.Controls;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Gui;

/// <summary>
///     OverlaySettings.SpeedCorrectionPercent as edited in the welcome window and Settings' Speed tab: the percentage, and a
///     calculator that works it out from a known top speed (SpeedCalibration.PercentFor). The owner reads Percent and saves it.
/// </summary>
public partial class SpeedCalibrationEditor : UserControl
{
	private double _savedPercent;
	private double? _recordingTopSpeedKmh;
	private double? _calculatedPercent;

	public SpeedCalibrationEditor()
	{
		InitializeComponent();
		CorrectionBox.Maximum = (decimal)SpeedCalibration.MaxPercent;
		CorrectionBox.ValueChanged += (_, _) => UpdateCorrectionText();
		RealSpeedBox.ValueChanged += (_, _) => UpdateCalculation();
		ShownSpeedBox.ValueChanged += (_, _) => UpdateCalculation();
		ApplyButton.Click += (_, _) => ApplyCalculation();
		UseRecordingButton.Click += (_, _) =>
		{
			if (_recordingTopSpeedKmh is { } measured)
				ShownSpeedBox.Value = (decimal)Math.Round(SpeedCalibration.Corrected(measured, _savedPercent), 2);
		};
		UpdateCorrectionText();
		UpdateCalculation();
	}

	/// <summary>The correction as edited, within SpeedCalibration's range.</summary>
	public double Percent => SpeedCalibration.Clamp((double)(CorrectionBox.Value ?? 0));

	/// <summary>Whether Enter in one of the calculator's fields should apply its result.</summary>
	public bool HasCalculation => _calculatedPercent is not null;

	/// <param name="savedPercent">The correction in settings.json - what a top speed the overlay showed already carries.</param>
	/// <param name="recordingTopSpeedKmh">The loaded recording's top speed as measured (no correction), to fill in exactly; null when none is loaded.</param>
	public void Load(double savedPercent, double? recordingTopSpeedKmh)
	{
		_savedPercent = SpeedCalibration.Clamp(savedPercent);
		_recordingTopSpeedKmh = recordingTopSpeedKmh is > 0 ? recordingTopSpeedKmh : null;
		CorrectionBox.Value = (decimal)_savedPercent;

		UseRecordingButton.IsVisible = _recordingTopSpeedKmh is not null;
		if (_recordingTopSpeedKmh is { } measured)
			UseRecordingButton.Content = $"Use the loaded recording's top speed ({SpeedCalibration.Corrected(measured, _savedPercent):0.##} km/h)";
		UpdateCalculation();
	}

	/// <summary>Puts the worked-out correction into the field. False when there's nothing to apply.</summary>
	public bool ApplyCalculation()
	{
		if (_calculatedPercent is not { } percent) return false;

		CorrectionBox.Value = (decimal)percent;
		RealSpeedBox.Value = null;
		ShownSpeedBox.Value = null;
		ResultText.Foreground = Palette.Success;
		ResultText.Text = $"Correction set to {percent:0.0}%. It's saved with the other settings.";
		return true;
	}

	private void UpdateCorrectionText()
	{
		double percent = Percent;
		CorrectionText.Text = percent > 0
			? $"Every speed is raised by {percent:0.0}%: a measured 20 km/h is shown as {SpeedCalibration.Corrected(20, percent):0.0} km/h. " +
			  "Distance, route and tilt keep the measured values."
			: "Off - speeds are shown as the GPS measured them.";
	}

	private void UpdateCalculation()
	{
		_calculatedPercent = RealSpeedBox.Value is { } real && ShownSpeedBox.Value is { } shown
			? SpeedCalibration.PercentFor((double)real, (double)shown, _savedPercent)
			: null;

		ApplyButton.IsVisible = _calculatedPercent is { } candidate && Math.Abs(candidate - Percent) > 1e-9;
		ResultText.Foreground = _calculatedPercent is null ? Palette.TextMuted : Palette.TextPrimary;
		if (_calculatedPercent is not { } percent)
		{
			ResultText.Text = "Example: a scooter that tops out at 25 km/h, shown peaking at 22 - enter 25 and 22.";
			return;
		}

		double measured = (double)ShownSpeedBox.Value!.Value / SpeedCalibration.Factor(_savedPercent);
		ApplyButton.Content = $"Use {percent:0.0}%";
		ResultText.Text = percent <= 0
			? "The overlay already shows this speed or more - no correction needed."
			: $"With {percent:0.0}% the top speed is shown as {SpeedCalibration.Corrected(measured, percent):0.0} instead of {(double)ShownSpeedBox.Value:0.0}.";
	}
}
