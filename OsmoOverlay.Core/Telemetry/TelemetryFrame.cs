namespace OsmoOverlay.Core.Telemetry;

public sealed record TelemetryFrame(
	int FrameNumber,
	double SampleTimeSeconds,
	double Latitude,
	double Longitude,
	double AltitudeMeters,
	// UTC by the GPS clock. A camera format gives the receiver's own whole seconds; ExtractCombined turns them into
	// one clock running with the video, from the recording's very first frame and restarted only after a stop (TelemetryExtraction.ApplyGpsClock).
	DateTime? GpsTimestamp,
	double AccelX,
	double AccelY,
	double AccelZ,
	double? GpsSpeedMs = null,
	float? Iso = null,
	double? ShutterSeconds = null,
	int? ColorTemperatureKelvin = null,
	// False only when this sample had no real GPS fix (raw "no fix"/dropped signal) and the camera format
	// carried Latitude/Longitude/AltitudeMeters forward from the last fix instead, or left them at (0,0)
	// before the recording's first one (DJI: GpsForwardFill) - not raised
	// for the normal, much more frequent case of the GPS receiver simply not having reported a *new*
	// fix yet at this exact video frame (see GpsInterpolation for that one).
	bool HasGpsFix = true,
	// Set only on frames moved onto a cut render's own timeline (OutputTimeline.MapFrames), where
	// SampleTimeSeconds becomes the time in the output video - this keeps where it was in the recording.
	double? SourceTimeSeconds = null,
	// First frame of a file recorded after the camera had stopped (TelemetryExtraction.ExtractCombined) - next
	// to the previous file on the timeline but not in time, so speed, distance and the route don't run across it.
	bool StartsAfterGap = false,
	// What GpsOutliers filled in on this frame - an implausible position or receiver speed (the GPS alert widget says so).
	GpsCorrection GpsCorrected = GpsCorrection.None,
	// The camera's orientation where its format reports one (DJI). Kept beside the accelerometer, not instead of it:
	// nothing in Core takes a direction from it yet.
	TelemetryOrientation? Orientation = null,
	double? AmbientTemperatureCelsius = null,
	double? ImuTemperatureCelsius = null,
	double? DeviceDistanceMeters = null,
	double? DeviceBatteryPercent = null,
	double? AudioRmsDbfs = null,
	double? AudioPeakDbfs = null,
	bool? WindProcessingActive = null,
	double? WindProcessingLevel = null,
	double? ImageLuma = null,
	// A drone's flight log: height above the take-off point and the distance from it (an external track, ExternalTrackMerge).
	double? HeightAboveHomeMeters = null,
	double? DistanceFromHomeMeters = null,
	// ... and its climb rate (up is positive) and the satellites the aircraft had.
	double? VerticalSpeedMs = null,
	int? GpsSatellites = null,
	// ... its battery, and what the log says happened at this frame (FlightEvent), for the drone battery and flight alerts widgets.
	DroneBattery? DroneBattery = null,
	IReadOnlyList<FlightEvent>? FlightEvents = null,
	// ... how good its GPS is (0-5 bars, the aircraft's own level) and its radio links.
	int? GpsLevel = null,
	DroneSignal? DroneSignal = null,
	// ... and the flight mode it is in (P-GPS, Sport).
	string? FlightMode = null,
	// ... the remote controller's sticks and the aircraft's attitude (the sticks, attitude indicator and 3D orientation widgets).
	DroneSticks? DroneSticks = null,
	DroneAttitude? DroneAttitude = null)
{
	public double GForce => Math.Sqrt(AccelX * AccelX + AccelY * AccelY + AccelZ * AccelZ);

	/// <summary>Seconds since the recording's own first frame, whichever timeline SampleTimeSeconds is on.</summary>
	public double RecordingTimeSeconds => SourceTimeSeconds ?? SampleTimeSeconds;
}

/// <summary>Which GPS readings of a frame GpsOutliers repaired.</summary>
[Flags]
public enum GpsCorrection
{
	None = 0,
	Position = 1,
	Speed = 2
}
