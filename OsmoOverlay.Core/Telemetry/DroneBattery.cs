namespace OsmoOverlay.Core.Telemetry;

/// <summary>
///     A drone's battery as its flight log reports it: charge, pack voltage, current drawn (positive), temperature and the cells'
///     voltages. Held from the latest reading, never interpolated.
/// </summary>
public sealed record DroneBattery(
	double Percent,
	double? VoltageV = null,
	double? CurrentA = null,
	double? TemperatureCelsius = null,
	IReadOnlyList<double>? CellVoltages = null,
	double? CapacityMah = null)
{
	public double? MinCellVoltage => CellVoltages is { Count: > 0 } cells ? cells.Min() : null;
}

/// <summary>
///     The quality of a drone's radio links in percent, as the flight log last said them - null where it hasn't said one. Held from
///     the latest reading, never interpolated.
/// </summary>
public sealed record DroneSignal(double? UplinkPercent = null, double? DownlinkPercent = null);

/// <summary>
///     The remote controller's sticks as the flight log last said them, each from -1 to 1: aileron (right stick, right is positive),
///     elevator (right stick, forward is positive), throttle (left stick, up is positive) and rudder (left stick, right is positive).
///     Held from the latest reading, never interpolated.
/// </summary>
public sealed record DroneSticks(double Aileron, double Elevator, double Throttle, double Rudder);

/// <summary>
///     The aircraft's own attitude from the flight log, in degrees: pitch (nose up is positive), roll (right bank is positive) and
///     yaw (the heading, clockwise from north).
/// </summary>
public sealed record DroneAttitude(double PitchDegrees, double RollDegrees, double YawDegrees);

public enum FlightEventKind
{
	FlightMode,
	MotorsOn,
	MotorsOff,
	BatteryStart,
	BatteryLevel,
	BatteryEnd,
	ReturnAltitude,
	AltitudeLimit,
	FinalPosition,
	LowVoltage,
	CriticalVoltage,
	Tip,
	Warning
}

/// <summary>
///     Something the flight log says happened, for the flight alerts widget. `Text` is the flight mode's name or the message the
///     app wrote into the log (in the app's own language); `Value` the percent or the cell voltage; `Code` the app's message code.
/// </summary>
public sealed record FlightEvent(FlightEventKind Kind, string? Text = null, double? Value = null, string? Code = null);
