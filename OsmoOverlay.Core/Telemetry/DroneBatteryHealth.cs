namespace OsmoOverlay.Core.Telemetry;

public enum BatteryCondition
{
	Normal,
	Caution,
	Critical
}

/// <summary>
///     How a drone battery's readings stand, for coloring them: normal is the widget's plain text, caution and critical take its low and
///     critical colors. Voltage defaults follow the generic LiPo guidance in https://betaflight.com/docs/wiki/app/power-tab.
///     These are display heuristics, not DJI firmware limits or a battery fault diagnosis; the widget lets the user set its own.
/// </summary>
public static class DroneBatteryHealth
{
	public const double LowCellVolts = 3.5;
	public const double CriticalCellVolts = 3.3;
	public const double ColdCautionCelsius = 10, ColdCriticalCelsius = 0;
	public const double HotCautionCelsius = 40, HotCriticalCelsius = 50;

	/// <summary>The lowest cell sets the condition of the pack's voltage and of the cells' line; without cells nothing is known.</summary>
	public static BatteryCondition Cells(DroneBattery? battery, double low = LowCellVolts, double critical = CriticalCellVolts)
	{
		return battery?.MinCellVoltage is { } lowest && double.IsFinite(lowest)
			? lowest <= critical ? BatteryCondition.Critical : lowest <= low ? BatteryCondition.Caution : BatteryCondition.Normal
			: BatteryCondition.Normal;
	}

	public static BatteryCondition Temperature(double? celsius, double coldCaution = ColdCautionCelsius, double coldCritical = ColdCriticalCelsius,
		double hotCaution = HotCautionCelsius, double hotCritical = HotCriticalCelsius)
	{
		if (celsius is not { } value || !double.IsFinite(value)) return BatteryCondition.Normal;

		return value <= coldCritical || value >= hotCritical ? BatteryCondition.Critical
			: value < coldCaution || value >= hotCaution ? BatteryCondition.Caution
			: BatteryCondition.Normal;
	}
}
