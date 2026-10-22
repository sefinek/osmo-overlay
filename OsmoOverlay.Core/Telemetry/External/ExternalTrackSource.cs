using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>
///     A track file to put onto a recording's telemetry. `OffsetSeconds` is how far the track's clock is ahead of the camera's
///     (the recording at 14:27:45 by the camera, 14:20:49 by the track: -416) - null to find it (ExternalTrackSync).
/// </summary>
public sealed record ExternalTrackSource(string Path, double? OffsetSeconds = null)
{
	/// <summary>
	///     The frames with the track's position, altitude, speed and time in them (ExternalTrackMerge). `recordingStartUtc` is the
	///     container's creation time - the camera's clock at the first frame. Throws InvalidDataException for a file that isn't a
	///     track, InvalidOperationException when there is no way to tell where the recording lies on the track.
	/// </summary>
	public IReadOnlyList<TelemetryFrame> Apply(IReadOnlyList<TelemetryFrame> frames, double fps, DateTime? recordingStartUtc)
	{
		ExternalTrack track = ExternalTrackReader.Read(Path);
		string name = System.IO.Path.GetFileName(Path);
		AppLogger.Info($"Track {name}: {track.Points.Count} fixes {track.StartUtc:HH:mm:ss}-{track.EndUtc:HH:mm:ss} UTC, " +
		               $"{track.Pauses.Count} stop(s) of the timer, {(track.Points.Any(p => p.SpeedMs is not null) ? "with" : "without")} speed");

		DateTime trackTimeAtStart;
		if (OffsetSeconds is { } offset)
		{
			if (recordingStartUtc is not { } start) throw new InvalidOperationException(CoreStrings.ExternalTrack_NoStartTime);

			trackTimeAtStart = start.AddSeconds(offset);
			AppLogger.Info($"Track {name}: the recording's start on the track's clock {trackTimeAtStart:HH:mm:ss} UTC (offset {offset:+0.##;-0.##} s, given)");
		}
		else
		{
			TrackSyncEstimate estimate = ExternalTrackSync.Estimate(frames, fps, recordingStartUtc, track) ??
			                             throw new InvalidOperationException(string.Format(CoreStrings.ExternalTrack_NoMatch, name));
			trackTimeAtStart = estimate.TrackTimeAtVideoStartUtc;
			string how = estimate.Method == TrackSyncMethod.Clock
				? "the clocks agree"
				: $"matched by motion, correlation {estimate.Correlation:0.00}";
			AppLogger.Info($"Track {name}: the recording's start on the track's clock {trackTimeAtStart:HH:mm:ss} UTC ({how})");
		}

		return ExternalTrackMerge.Apply(frames, track, trackTimeAtStart);
	}
}
