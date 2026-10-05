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
	public event Action<double>? PercentChanged;

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
	public double? RealSpeedValue => RealSpeedBox.Value is { } value ? (double)value : null;
	public double? GpsSpeedValue => ShownSpeedBox.Value is { } value ? (double)value : null;

	public void SetComparisonSpeeds(double? actual, double? measured)
	{
		RealSpeedBox.Value = actual is { } real ? (decimal)real : null;
		ShownSpeedBox.Value = measured is { } gps ? (decimal)gps : null;
	}

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
			UseRecordingButton.Content = string.Format(Strings.Calibration_UseRecording, measured);
			ShownSpeedBox.Value = (decimal)Math.Round(measured, 2);
		}

		UpdateCalculation();
	}

	/// <summary>Puts the worked-out correction into the field. False when there's nothing to apply.</summary>
	public bool ApplyCalculation()
	{
		if (_calculatedPercent is not { } percent) return false;

		CorrectionBox.Value = (decimal)percent;
		ApplyButton.IsVisible = false;
		ResultText.Foreground = Palette.Success;
		ResultText.Text = string.Format(Strings.Calibration_Set, percent);
		return true;
	}

	private void UpdateCorrectionText()
	{
		double percent = Percent;
		CorrectionText.Text = percent > 0
			? string.Format(Strings.Calibration_Raised, percent, SpeedCalibration.Corrected(46, percent))
			: Strings.Calibration_Off;
		UpdateCalculation();
		PercentChanged?.Invoke(percent);
	}

	private void UpdateCalculation()
	{
		UseRecordingButton.IsEnabled = _recordingCruisingSpeedKmh is { } recorded &&
			ShownSpeedBox.Value != (decimal)Math.Round(recorded, 2);
		_calculatedPercent = RealSpeedBox.Value is { } real && ShownSpeedBox.Value is { } shown
			? SpeedCalibration.PercentFor((double)real, (double)shown, 0)
			: null;

		ApplyButton.IsVisible = _calculatedPercent is { } candidate && Math.Abs(candidate - Percent) > 1e-9;
		ResultText.Foreground = Palette.TextPrimary;
		if (_calculatedPercent is not { } percent)
		{
			ResultText.Text = Strings.Calibration_Example;
			return;
		}

		double measured = (double)ShownSpeedBox.Value!.Value;
		double corrected = SpeedCalibration.Corrected(measured, percent);
		ApplyButton.Content = string.Format(Strings.Calibration_Use, percent);
		ResultText.Text = percent <= 0
			? Strings.Calibration_NoneNeeded
			: string.Format(Strings.Calibration_Result, percent, measured, corrected);
	}
}
