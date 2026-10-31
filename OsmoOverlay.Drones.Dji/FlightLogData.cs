using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Drones.Dji;

/// <summary>
///     One OSD record of the flight, `Seconds` from the log's first one (10 per second). Height is above the take-off point;
///     the vertical speed (up is positive) is worked out from it, see FlightLog.Decode.
/// </summary>
public sealed record FlightSample(
	double Seconds,
	double Latitude,
	double Longitude,
	double HeightMeters,
	double AltitudeMeters,
	double SpeedMs,
	int Satellites,
	bool GpsValid,
	double? VerticalSpeedMs = null,
	int? GpsLevel = null,
	string? FlightMode = null,
	DroneAttitude? Attitude = null);

/// <summary>The flight's totals from the log's details: distance flown, time, the greatest height and horizontal speed.</summary>
public sealed record FlightTotals(double DistanceKilometers, double DurationSeconds, double MaxHeightMeters, double MaxSpeedMs);

/// <summary>A flight log's start and totals from its details block alone (FlightLog.ReadHeader).</summary>
public sealed record FlightLogHeader(DateTime? StartedUtc, FlightTotals? Totals);

/// <summary>The battery as the log last said it, `Seconds` on the same clock as FlightSample.Seconds.</summary>
public sealed record FlightBatterySample(double Seconds, DroneBattery Battery);

/// <summary>The radio links as the log last said them, `Seconds` on the same clock as FlightSample.Seconds.</summary>
public sealed record FlightSignalSample(double Seconds, DroneSignal Signal);

/// <summary>The remote controller's sticks as the log last said them, `Seconds` on the same clock as FlightSample.Seconds.</summary>
public sealed record FlightSticksSample(double Seconds, DroneSticks Sticks);

/// <summary>Something that happened (a mode change, a message, a battery level), `Seconds` on the same clock as FlightSample.Seconds.</summary>
public sealed record FlightLogEvent(double Seconds, FlightEvent Event);

/// <summary>Where the aircraft took off, from the log's home record.</summary>
public readonly record struct FlightHome(double Latitude, double Longitude);

/// <summary>A stretch of the flight the aircraft's camera was recording, on the same clock as FlightSample.Seconds.</summary>
public readonly record struct FlightRecording(double StartSeconds, double EndSeconds);

/// <summary>
///     What a flight log says about the flight. `ClockUtc` is the UTC of Seconds = 0 by the clock of the app that wrote the log
///     (the middle of what its time stamps say) - null when the log has none. That clock is not always right; a recording's
///     place in the flight is more reliably told by `Recordings`.
/// </summary>
public sealed record FlightLogData(IReadOnlyList<FlightSample> Samples, IReadOnlyList<FlightRecording> Recordings, DateTime? ClockUtc, FlightHome? Home,
	IReadOnlyList<FlightBatterySample> Batteries, IReadOnlyList<FlightLogEvent> Events, IReadOnlyList<FlightSignalSample> Signals,
	string? AircraftName, IReadOnlyList<FlightSticksSample> Sticks);
