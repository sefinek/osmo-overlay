using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Core.Telemetry.External;

public sealed record ExternalTrackPlayback(ExternalTrack Track, TrackSynchronization Synchronization,
	IReadOnlyList<TelemetryFrame> RawFrames, IReadOnlyList<DerivedFrame> DerivedFrames, double RecordingSeconds,
	bool SmoothGpsMotion, double SpeedCorrectionPercent)
{
	public double DurationSeconds => (Track.EndUtc - Track.StartUtc).TotalSeconds;
	public double? VideoPosition(double seconds) => ExternalTrackPreview.VideoPosition(Track, Synchronization, seconds, RecordingSeconds);
}

public static class ExternalTrackPreview
{
	private const int MaxIntervals = 100_000;

	public static ExternalTrackPlayback Prepare(ExternalTrack track, TrackSynchronization sync, double recordingSeconds, OverlaySettings settings)
	{
		sync.Validate();
		List<TelemetryFrame> raw = Frames(track);
		return new(track, sync, raw, TelemetryProcessor.Process(raw, null, settings.SmoothGpsMotion, settings.SpeedCorrectionPercent), recordingSeconds,
			settings.SmoothGpsMotion, settings.SpeedCorrectionPercent);
	}

	public static bool ExtendsRecording(ExternalTrack track, TrackSynchronization sync, double recordingSeconds, double toleranceSeconds = 0.1)
	{
		sync.Validate();
		if (!double.IsFinite(recordingSeconds) || recordingSeconds <= 0) return false;
		return (sync.At(0) - track.StartUtc).TotalSeconds > toleranceSeconds ||
		       (track.EndUtc - sync.At(recordingSeconds)).TotalSeconds > toleranceSeconds;
	}

	public static TimeRange? RecordingRange(ExternalTrack track, TrackSynchronization sync, double recordingSeconds)
	{
		sync.Validate();
		if (!double.IsFinite(recordingSeconds) || recordingSeconds <= 0) return null;
		double duration = (track.EndUtc - track.StartUtc).TotalSeconds;
		double start = Math.Clamp((sync.At(0) - track.StartUtc).TotalSeconds, 0, duration);
		double end = Math.Clamp((sync.At(recordingSeconds) - track.StartUtc).TotalSeconds, 0, duration);
		return end > start ? new TimeRange(start, end) : null;
	}

	public static double? VideoPosition(ExternalTrack track, TrackSynchronization sync, double trackSeconds, double recordingSeconds)
	{
		sync.Validate();
		if (!double.IsFinite(trackSeconds) || !double.IsFinite(recordingSeconds) || recordingSeconds <= 0) return null;
		double seconds = RecordingPosition(track, sync, trackSeconds);
		return seconds >= 0 && seconds < recordingSeconds ? seconds : null;
	}

	public static double RecordingPosition(ExternalTrack track, TrackSynchronization sync, double trackSeconds)
		=> sync.VideoSecondsAt(track.StartUtc) + trackSeconds / sync.Rate;

	/// <summary>A separate, bounded 10 Hz timeline: no camera sensor data is invented or copied beyond the recording.</summary>
	public static List<TelemetryFrame> Frames(ExternalTrack track, CancellationToken ct = default)
	{
		double duration = (track.EndUtc - track.StartUtc).TotalSeconds;
		int intervals = (int)Math.Min(MaxIntervals, Math.Ceiling(duration * 10));
		var frames = new List<TelemetryFrame>(intervals + 1);
		for (int i = 0; i <= intervals; i++)
		{
			ct.ThrowIfCancellationRequested();
			double seconds = i == intervals ? duration : i * duration / intervals;
			frames.Add(new TelemetryFrame(i, seconds, 0, 0, 0, null, 0, 0, 0, HasGpsFix: false));
		}
		List<TelemetryFrame> merged = ExternalTrackMerge.Apply(frames, track, track.StartUtc);
		for (int i = 1; i < merged.Count; i++)
			if (merged[i].HasGpsFix && !merged[i - 1].HasGpsFix) merged[i] = merged[i] with { StartsAfterGap = true };
		ct.ThrowIfCancellationRequested();
		return merged;
	}
}
