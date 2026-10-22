namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>
///     Puts another device's track onto a recording's telemetry frames - the camera has no GPS of its own (a GoPro HERO12), or a
///     better one beside it. Where the track covers a frame its position, altitude and speed replace the camera's and the
///     frame's time becomes the track's clock; the rest of the frame (the camera's motion, exposure) is untouched.
///     `trackTimeAtVideoStartUtc` is the moment on the track's clock of the recording's first frame.
///     A stop in the track (ExternalTrackState.Paused) holds the camera where it was, speed 0, as it did; a lost signal
///     leaves the frame without a fix (HasGpsFix false, the last position held), like any camera's. Frames the track
///     doesn't reach keep what the camera had.
/// </summary>
public static class ExternalTrackMerge
{
	public static List<TelemetryFrame> Apply(IReadOnlyList<TelemetryFrame> frames, ExternalTrack track, DateTime trackTimeAtVideoStartUtc)
	{
		var result = new List<TelemetryFrame>(frames.Count);
		var fill = new GpsForwardFill();
		int cursor = 0;
		foreach (TelemetryFrame frame in frames)
		{
			DateTime utc = trackTimeAtVideoStartUtc.AddSeconds(frame.SampleTimeSeconds);
			DateTime? wholeSecond = new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
			if (track.At(utc, ref cursor) is not { } sample)
			{
				result.Add(HoldWhereThereIsNoPosition(frame, fill));
				continue;
			}

			if (sample.State == ExternalTrackState.Lost)
			{
				(double lat, double lon, double altitude, _) = fill.Apply(null, null, null);
				result.Add(frame with
				{
					Latitude = lat, Longitude = lon, AltitudeMeters = altitude, HasGpsFix = false, GpsSpeedMs = null, GpsTimestamp = wholeSecond
				});
				continue;
			}

			(double latitude, double longitude, double altitudeMeters, _) = fill.Apply(sample.Latitude, sample.Longitude, sample.AltitudeMeters);
			result.Add(frame with
			{
				Latitude = latitude, Longitude = longitude, AltitudeMeters = altitudeMeters, HasGpsFix = true, GpsSpeedMs = sample.SpeedMs, GpsTimestamp = wholeSecond
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
