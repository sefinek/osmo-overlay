using System.Globalization;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Drones.Dji;

/// <summary>
///     A DJI drone's flight log (FlightRecord_*.txt) as a track for a recording: position, altitude, speed and battery at 10 Hz.
///     The drone's own camera writes no telemetry into its MP4, so the log is the one source - it goes the way a FIT or GPX
///     track does. Time is the log's clock (the app's), which can be off; the recording's place is then told by an anchor or
///     by motion, like for any other track.
/// </summary>
public sealed class DjiFlightLogFormat : ITrackFormat
{
	private const double FindSlackSeconds = 60;

	public string DisplayName => "DJI flight log";

	public IReadOnlyList<string> Extensions => [".txt"];

	public bool Recognizes(string path, ReadOnlySpan<byte> start)
	{
		return Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) && FlightLog.LooksLikeFlightLog(start);
	}

	public ExternalTrack Read(string path)
	{
		FlightLog log = FlightLog.Read(path);
		FlightLogData data = log.Decode(log.IsEncrypted ? DjiKeychains.Obtain(log) : null);
		ExternalTrack track = ToTrack(log, data);
		NotifySummary(log, data);
		return track;
	}

	private static void NotifySummary(FlightLog log, FlightLogData data)
	{
		if (log.Totals is not { } totals) return;

		AppLogger.Notify(string.Format(CoreStrings.DjiLog_Summary, data.AircraftName ?? "DJI", TimeSpan.FromSeconds(totals.DurationSeconds).ToString(@"m\:ss"), totals.DistanceKilometers.ToString("0.00", CultureInfo.InvariantCulture),
			totals.MaxHeightMeters.ToString("0", CultureInfo.InvariantCulture), (totals.MaxSpeedMs * 3.6).ToString("0", CultureInfo.InvariantCulture)));
	}

	/// <summary>
	///     A log in the recording's folder, or in the folder the logs fetched from a phone are kept (PhoneFlightLogs), whose flight (from its start to its duration, both in the details block that needs no keys)
	///     covers the recording's start. The log's clock is the app's, so a log that is off is not found - the log can still be added by hand.
	///     With several, the one that started latest before the recording.
	/// </summary>
	public string? FindFor(string recordingPath, DateTime recordingStartUtc)
	{
		string?[] folders = [Path.GetDirectoryName(recordingPath), PhoneFlightLogs.CacheFolder];
		string? best = null;
		DateTime bestStart = DateTime.MinValue;
		foreach (string candidate in folders.Where(f => f is not null && Directory.Exists(f)).Distinct().SelectMany(f => Directory.EnumerateFiles(f!, "*.txt")).Where(c => PhoneFlightLogs.IsLogName(Path.GetFileName(c))))
		{
			FlightLogHeader log;
			try
			{
				log = FlightLog.ReadHeader(candidate);
			}
			catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
			{
				continue;
			}

			if (log.StartedUtc is not { } started || log.Totals is not { } totals) continue;
			if (recordingStartUtc < started.AddSeconds(-FindSlackSeconds) || recordingStartUtc > started.AddSeconds(totals.DurationSeconds + FindSlackSeconds)) continue;
			if (started > bestStart)
			{
				best = candidate;
				bestStart = started;
			}
		}

		return best;
	}

	internal static ExternalTrack ToTrack(FlightLog log, FlightLogData data)
	{
		DateTime? clock = data.ClockUtc ?? log.StartedUtc;
		if (clock is not { } start) throw new InvalidDataException("The flight log has no time stamps.");

		List<ExternalTrackPoint> points = [];
		foreach (FlightSample sample in data.Samples)
		{
			if (!sample.GpsValid && sample.Satellites == 0) continue;

			DateTime utc = start.AddSeconds(sample.Seconds);
			points.Add(new ExternalTrackPoint(utc, sample.Latitude, sample.Longitude, sample.AltitudeMeters, sample.SpeedMs,
				HeightMeters: sample.HeightMeters, VerticalSpeedMs: sample.VerticalSpeedMs, Satellites: sample.Satellites, GpsLevel: sample.GpsLevel, FlightMode: sample.FlightMode,
					Attitude: sample.Attitude));
		}

		if (points.Count < 2) throw new InvalidDataException(CoreStrings.DjiLog_NoPositions);
		AppLogger.Info($"DJI flight log: version {log.Version}, {data.Samples.Count} samples, {points.Count} with a fix, {data.Recordings.Count} recording(s) of the camera");
		List<ExternalTrackEvent> events = [.. data.Events.Select(e => new ExternalTrackEvent(start.AddSeconds(e.Seconds), e.Event))];
		if (data.Home is { } takeOff && events.LastOrDefault(e => e.Event.Kind == FlightEventKind.MotorsOff) is { } motorsOff)
		{
			ExternalTrackPoint last = points[^1];
			double away = TelemetryMath.HaversineMeters(takeOff.Latitude, takeOff.Longitude, last.Latitude, last.Longitude);
			events.Add(new ExternalTrackEvent(motorsOff.Utc, new FlightEvent(FlightEventKind.FinalPosition, Value: Math.Round(away, 1))));
		}

		IEnumerable<ExternalTrackRecording> recordings = data.Recordings.Select(r => new ExternalTrackRecording(start.AddSeconds(r.StartSeconds), start.AddSeconds(r.EndSeconds)));
		return new ExternalTrack(points, recordings: recordings,
			home: data.Home is { } home ? new ExternalTrackHome(home.Latitude, home.Longitude) : null,
			droneBatteries: data.Batteries.Select(b => new ExternalDroneBatteryReading(start.AddSeconds(b.Seconds), b.Battery)),
			events: events,
			droneSignals: data.Signals.Select(r => new ExternalDroneSignalReading(start.AddSeconds(r.Seconds), r.Signal)),
			deviceName: data.AircraftName,
			droneSticks: data.Sticks.Select(r => new ExternalDroneSticksReading(start.AddSeconds(r.Seconds), r.Sticks)));
	}
}
