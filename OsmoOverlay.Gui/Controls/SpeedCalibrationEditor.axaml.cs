using Avalonia.Controls;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Gui;

/// <summary>
///     OverlaySettings.SpeedCorrectionPercent as edited in the welcome window and Settings' Speed tab: the percentage, and a
///     calculator that works it out from a known cruising speed (SpeedCalibration.PercentFor). The calculator takes the measured
///     speed - before any correction - so its result never depends on the correction already set. The owner reads Percent and saves it.
/// </summary>
public partial class SpeedCalibrationEditor : UserControl
{
	private double? _recordingCruisingSpeedKmh;
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
			if (_recordingCruisingSpeedKmh is { } measured) ShownSpeedBox.Value = (decimal)Math.Round(measured, 2);
		};
		UpdateCorrectionText();
		UpdateCalculation();
	}

	/// <summary>The correction as edited, within SpeedCalibration's range.</summary>
	public double Percent => SpeedCalibration.Clamp((double)(CorrectionBox.Value ?? 0));

	/// <summary>Whether Enter in one of the calculator's fields should apply its result.</summary>
	public bool HasCalculation => _calculatedPercent is not null;

	/// <param name="savedPercent">The correction in settings.json.</param>
	/// <param name="recordingCruisingSpeedKmh">The loaded recording's cruising speed as measured (no correction, SpeedCalibration.CruisingSpeedKmh), to fill in; null when none is loaded.</param>
	public void Load(double savedPercent, double? recordingCruisingSpeedKmh)
	{
		_recordingCruisingSpeedKmh = recordingCruisingSpeedKmh is > 0 ? recordingCruisingSpeedKmh : null;
		CorrectionBox.Value = (decimal)SpeedCalibration.Clamp(savedPercent);

		// Filled in straight away: the exact value, so nobody reads a rounded or already corrected one off the screen instead.
		UseRecordingButton.IsVisible = _recordingCruisingSpeedKmh is not null;
		if (_recordingCruisingSpeedKmh is { } measured)
		{
			UseRecordingButton.Content = $"Reset to the loaded recording's cruising speed ({measured:0.##} km/h)";
			ShownSpeedBox.Value = (decimal)Math.Round(measured, 2);
		}

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
			? SpeedCalibration.PercentFor((double)real, (double)shown, 0)
			: null;

		ApplyButton.IsVisible = _calculatedPercent is { } candidate && Math.Abs(candidate - Percent) > 1e-9;
		ResultText.Foreground = _calculatedPercent is null ? Palette.TextMuted : Palette.TextPrimary;
		if (_calculatedPercent is not { } percent)
		{
			ResultText.Text = "Example: the speedometer reads 25 at full speed, the GPS measured 22 - enter 25 and 22.";
			return;
		}

		double measured = (double)ShownSpeedBox.Value!.Value;
		double corrected = SpeedCalibration.Corrected(measured, percent);
		ApplyButton.Content = $"Use {percent:0.0}%";
		ResultText.Text = percent <= 0
			? "The GPS already measured this speed or more - no correction needed."
			: $"With {percent:0.0}% the measured {measured:0.##} becomes {corrected:0.##} - the widget shows {Math.Round(corrected, MidpointRounding.AwayFromZero):0}. " +
			  "Rounded down to 0.1%, so it never comes out above the real speed.";
	}
}
