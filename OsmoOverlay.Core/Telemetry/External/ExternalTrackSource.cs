using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Telemetry.External;

/// <summary>
///     A track file to put onto a recording's telemetry. `OffsetSeconds` is how far the track's clock is ahead of the camera's
///     clock. Synchronization anchors work without a camera date and optionally correct clock drift.
/// </summary>
public sealed record ExternalTrackSource(string Path, double? OffsetSeconds = null, TrackSynchronization? Synchronization = null)
{
	public void Validate()
	{
		if (string.IsNullOrWhiteSpace(Path) || OffsetSeconds is { } offset && !double.IsFinite(offset) || OffsetSeconds is not null && Synchronization is not null)
			throw new InvalidDataException(CoreStrings.ExternalTrack_InvalidAnchors);
		Synchronization?.Validate();
	}

	public TrackSynchronization Resolve(IReadOnlyList<TelemetryFrame> frames, double fps, DateTime? recordingStartUtc, ExternalTrack track)
	{
		Validate();
		if (Synchronization is { } synchronization) return synchronization;
		if (OffsetSeconds is { } offset)
		{
			if (recordingStartUtc is not { } start) throw new InvalidOperationException(CoreStrings.ExternalTrack_NoStartTime);
			return new TrackSynchronization(new TrackSyncAnchor(0, start.AddSeconds(offset)));
		}
		TrackSyncEstimate estimate = ExternalTrackSync.Estimate(frames, fps, track) ??
		                             throw new InvalidOperationException(string.Format(CoreStrings.ExternalTrack_NoMatch, System.IO.Path.GetFileName(Path)));
		AppLogger.Warn(string.Format(CoreStrings.ExternalTrack_MotionEstimate, estimate.Correlation));
		return new TrackSynchronization(new TrackSyncAnchor(0, estimate.TrackTimeAtVideoStartUtc));
	}

	public FileSummary Apply(FileSummary summary)
	{
		IReadOnlyList<TelemetryFrame> frames = Apply(summary.TelemetryFrames ?? [], summary.Video.Fps, summary.ContainerRecordingStartUtc);
		List<DerivedFrame> derived = TelemetryProcessor.Process(frames, summary.CameraFormat);
		return summary with { TelemetryFrames = frames, DerivedFrames = derived, Telemetry = TelemetryProcessor.Summarize(derived), GpsClockShift = null };
	}

	/// <summary>
	///     The frames with the track's position, altitude, speed and time in them (ExternalTrackMerge). `recordingStartUtc` is the
	///     container's creation time - the camera's clock at the first frame. Throws InvalidDataException for a file that isn't a
	///     track, InvalidOperationException when there is no way to tell where the recording lies on the track.
	/// </summary>
	public IReadOnlyList<TelemetryFrame> Apply(IReadOnlyList<TelemetryFrame> frames, double fps, DateTime? recordingStartUtc)
	{
		Validate();
		ExternalTrack track = ExternalTrackReader.Read(Path);
		string name = System.IO.Path.GetFileName(Path);
		AppLogger.Info($"Track {name}: {track.Points.Count} fixes {track.StartUtc:HH:mm:ss}-{track.EndUtc:HH:mm:ss} UTC, " +
		               $"{track.Pauses.Count} stop(s) of the timer, {(track.Points.Any(p => p.SpeedMs is not null) ? "with" : "without")} speed");

		TrackSynchronization synchronization = Resolve(frames, fps, recordingStartUtc, track);
		if (frames.Count == 0 || synchronization.At(frames[0].SampleTimeSeconds) > track.EndUtc || synchronization.At(frames[^1].SampleTimeSeconds) < track.StartUtc)
			throw new InvalidOperationException(CoreStrings.ExternalTrack_NoOverlap);
		AppLogger.Info($"Track {name}: video start {synchronization.At(0):O}, clock rate {synchronization.Rate:0.#########}");
		return ExternalTrackMerge.Apply(frames, track, synchronization);
	}
}
