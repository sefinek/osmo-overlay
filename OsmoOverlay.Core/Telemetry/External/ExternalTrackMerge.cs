namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>
///     Puts another device's track onto a recording's telemetry frames - the camera has no GPS of its own (a GoPro HERO12), or a
///     better one beside it. Where the track covers a frame its position, altitude and speed replace the camera's and the
///     frame's time becomes the track's clock; the rest of the frame (the camera's motion, exposure) is untouched.
///     `trackTimeAtVideoStartUtc` is the moment on the track's clock of the recording's first frame.
///     Timer pauses and gaps leave the frame without a fix or speed, holding the last known position.
///     The mapped UTC clock continues across gaps and outside the track; outside it the camera's own GPS is retained.
/// </summary>
public static class ExternalTrackMerge
{
	public static List<TelemetryFrame> Apply(IReadOnlyList<TelemetryFrame> frames, ExternalTrack track, DateTime trackTimeAtVideoStartUtc)
		=> Apply(frames, track, new TrackSynchronization(new TrackSyncAnchor(0, trackTimeAtVideoStartUtc)));

	public static List<TelemetryFrame> Apply(IReadOnlyList<TelemetryFrame> frames, ExternalTrack track, TrackSynchronization synchronization)
	{
		synchronization.Validate();
		var result = new List<TelemetryFrame>(frames.Count);
		var fill = new GpsForwardFill();
		int cursor = 0;
		foreach (TelemetryFrame frame in frames)
		{
			DateTime utc = synchronization.At(frame.SampleTimeSeconds);
			ExternalTrackSample? reading = track.At(utc, ref cursor);
			TelemetryFrame merged = frame with
			{
				AmbientTemperatureCelsius = reading?.TemperatureCelsius,
				DeviceDistanceMeters = reading?.DistanceMeters,
				DeviceBatteryPercent = track.BatteryAt(utc)
			};
			if (reading is not { } sample)
			{
				result.Add(HoldWhereThereIsNoPosition(merged, fill) with { GpsTimestamp = utc });
				continue;
			}

			if (sample.State != ExternalTrackState.Fix)
			{
				(double lat, double lon, double altitude, _) = fill.Apply(null, null, null);
				result.Add(merged with
				{
					Latitude = lat, Longitude = lon, AltitudeMeters = altitude, HasGpsFix = false, GpsSpeedMs = null, GpsTimestamp = utc
				});
				continue;
			}

			(double latitude, double longitude, double altitudeMeters, _) = fill.Apply(sample.Latitude, sample.Longitude, sample.AltitudeMeters);
			result.Add(merged with
			{
				Latitude = latitude, Longitude = longitude, AltitudeMeters = altitudeMeters, HasGpsFix = true, GpsSpeedMs = sample.SpeedMs, GpsTimestamp = utc
			});
		}

		TelemetryExtraction.BackfillBeforeFirstFix(result);
		return result;
	}

	/// <summary>A frame the track doesn't reach keeps the camera's own GPS - or, with none, holds the last position the track gave.</summary>
	private static TelemetryFrame HoldWhereThereIsNoPosition(TelemetryFrame frame, GpsForwardFill fill)
	{
		if (frame.Latitude != 0 || frame.Longitude != 0)
		{
			if (frame.HasGpsFix) fill.Apply(frame.Latitude, frame.Longitude, frame.AltitudeMeters);
			return frame;
		}

		(double lat, double lon, double altitude, _) = fill.Apply(null, null, null);
		return frame with { Latitude = lat, Longitude = lon, AltitudeMeters = altitude };
	}
}
