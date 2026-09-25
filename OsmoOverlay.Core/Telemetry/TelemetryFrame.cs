namespace OsmoOverlay.Core.Telemetry;

public sealed record TelemetryFrame(
	int FrameNumber,
	double SampleTimeSeconds,
	double Latitude,
	double Longitude,
	double AltitudeMeters,
	DateTime? GpsTimestamp,
	double AccelX,
	double AccelY,
	double AccelZ,
	double? GpsSpeedMs = null,
	float? Iso = null,
	double? ShutterSeconds = null,
	int? ColorTemperatureKelvin = null,
	// False only when this sample had no real GPS fix (raw "no fix"/dropped signal) and
	// Latitude/Longitude/AltitudeMeters were carried forward by GpsForwardFill instead - not raised
	// for the normal, much more frequent case of the GPS receiver simply not having reported a *new*
	// fix yet at this exact video frame (see GpsInterpolation for that one).
	bool HasGpsFix = true,
	// Set only on frames moved onto a cut render's own timeline (OutputTimeline.MapFrames), where
	// SampleTimeSeconds becomes the time in the output video - this keeps where it was in the recording.
	double? SourceTimeSeconds = null,
	// First frame of a file recorded after the camera had stopped (TelemetryExtraction.ExtractCombined) - next
	// to the previous file on the timeline but not in time, so speed, distance and the route don't run across it.
	bool StartsAfterGap = false)
{
	public double GForce => Math.Sqrt(AccelX * AccelX + AccelY * AccelY + AccelZ * AccelZ);

	/// <summary>Seconds since the recording's own first frame, whichever timeline SampleTimeSeconds is on.</summary>
	public double RecordingTimeSeconds => SourceTimeSeconds ?? SampleTimeSeconds;
}
