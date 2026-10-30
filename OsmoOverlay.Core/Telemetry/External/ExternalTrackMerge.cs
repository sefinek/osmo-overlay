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
		var clock = new List<DateTime>(frames.Count);
		foreach (TelemetryFrame frame in frames)
		{
			DateTime utc = synchronization.At(frame.SampleTimeSeconds);
			clock.Add(utc);
			ExternalTrackSample? reading = track.At(utc, ref cursor);
			TelemetryFrame merged = frame with
			{
				AmbientTemperatureCelsius = reading?.TemperatureCelsius,
				DeviceDistanceMeters = reading?.DistanceMeters,
				DeviceBatteryPercent = track.BatteryAt(utc),
				DroneBattery = track.DroneBatteryAt(utc),
				DroneSignal = track.DroneSignalAt(utc),
				DroneSticks = track.DroneSticksAt(utc),
				DroneAttitude = reading is { State: ExternalTrackState.Fix } attitude ? attitude.Attitude : null,
				FlightMode = reading is { State: ExternalTrackState.Fix } moded ? moded.FlightMode : null,
				GpsLevel = reading is { State: ExternalTrackState.Fix } leveled ? leveled.GpsLevel : null,
				HeightAboveHomeMeters = reading is { State: ExternalTrackState.Fix } fix ? fix.HeightMeters : null,
				VerticalSpeedMs = reading is { State: ExternalTrackState.Fix } climbing ? climbing.VerticalSpeedMs : null,
				GpsSatellites = reading is { State: ExternalTrackState.Fix } counted ? counted.Satellites : null,
				DistanceFromHomeMeters = reading is { State: ExternalTrackState.Fix } at && track.Home is { } home
					? TelemetryMath.HaversineMeters(home.Latitude, home.Longitude, at.Latitude, at.Longitude)
					: null
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

		AttachEvents(result, clock, track);
		TelemetryExtraction.BackfillBeforeFirstFix(result);
		return result;
	}

	/// <summary>Each event goes onto the first frame at or after its moment - an event outside the recording is left out.</summary>
	private static void AttachEvents(List<TelemetryFrame> frames, List<DateTime> clock, ExternalTrack track)
	{
		if (frames.Count == 0 || track.Events.Count == 0) return;

		double frameSeconds = frames.Count > 1 ? (clock[^1] - clock[0]).TotalSeconds / (frames.Count - 1) : 0;
		var slack = TimeSpan.FromSeconds(Math.Max(frameSeconds, 0.001));
		foreach (ExternalTrackEvent trackEvent in track.Events)
		{
			if (trackEvent.Utc < clock[0] - slack || trackEvent.Utc > clock[^1] + slack) continue;

			int index = clock.BinarySearch(trackEvent.Utc);
			if (index < 0) index = Math.Min(~index, frames.Count - 1);
			frames[index] = frames[index] with { FlightEvents = [.. frames[index].FlightEvents ?? [], trackEvent.Event] };
		}
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
