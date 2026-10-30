using OsmoOverlay.Core.Cameras;
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

	/// <summary>
	///     A drone's flight log kept beside its footage: a drone's MP4 carries no telemetry of its own, so the log is the only source
	///     and is put on without being asked for. Null for any other camera, or when no log covers the recording's start.
	/// </summary>
	public static ExternalTrackSource? FindFor(ICameraFormat? camera, string recordingPath, DateTime? recordingStartUtc)
	{
		if (camera is not { IsDrone: true } || recordingStartUtc is not { } start) return null;

		string? found = ExternalTrackReader.FindFor(recordingPath, start);
		if (found is not null) AppLogger.Info($"Flight log {System.IO.Path.GetFileName(found)} found beside {System.IO.Path.GetFileName(recordingPath)}");
		return found is null ? null : new ExternalTrackSource(found);
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

		TrackAlignment alignment = Propose(frames, fps, recordingStartUtc, track) ??
		                           throw new InvalidOperationException(string.Format(CoreStrings.ExternalTrack_NoMatch, System.IO.Path.GetFileName(Path)));
		string name = System.IO.Path.GetFileName(Path);
		switch (alignment.Method)
		{
			case TrackAlignmentMethod.RecordingSpan:
				AppLogger.Info($"Track {name}: aligned by the camera's own recording span, video start {alignment.VideoStartUtc:O}");
				break;
			case TrackAlignmentMethod.ContainerClock:
				AppLogger.Info($"Track {name}: aligned by the container's creation time inside the camera's recording span, video start {alignment.VideoStartUtc:O}");
				break;
			default:
				AppLogger.Warn(string.Format(CoreStrings.ExternalTrack_MotionEstimate, alignment.Correlation));
				break;
		}

		return new TrackSynchronization(new TrackSyncAnchor(0, alignment.VideoStartUtc));
	}

	/// <summary>
	///     The automatic placement of a recording on a track, by the most exact way there is: the camera's own recording span, the container's
	///     creation time inside a span, then how both moved. Null when none of them says where it lies.
	/// </summary>
	public static TrackAlignment? Propose(IReadOnlyList<TelemetryFrame> frames, double fps, DateTime? recordingStartUtc, ExternalTrack track)
	{
		if (RecordingAnchor(frames, fps, track) is { } byRecording) return new TrackAlignment(byRecording, TrackAlignmentMethod.RecordingSpan);
		if (ContainingSpanAnchor(frames, fps, track, recordingStartUtc) is { } byClock) return new TrackAlignment(byClock, TrackAlignmentMethod.ContainerClock);
		return ExternalTrackSync.Estimate(frames, fps, track) is { } estimate ? new TrackAlignment(estimate.TrackTimeAtVideoStartUtc, TrackAlignmentMethod.Motion, estimate.Correlation) : null;
	}

	/// <summary>The share (0 to 1) of the recording's length that falls inside the track's time range under this synchronization.</summary>
	public static double Coverage(ExternalTrack track, TrackSynchronization synchronization, double videoSeconds)
	{
		if (videoSeconds <= 0) return 0;

		DateTime from = synchronization.At(0), to = synchronization.At(videoSeconds);
		double overlap = (Min(to, track.EndUtc) - Max(from, track.StartUtc)).TotalSeconds;
		return Math.Clamp(overlap / (to - from).TotalSeconds, 0, 1);

		static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
		static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
	}

	/// <summary>
	///     Where the recording's first frame lies on the track by the camera's own recording span: the one span as long as the
	///     frames (give or take a few seconds), or null when there is none or more than one.
	/// </summary>
	public static DateTime? RecordingAnchor(IReadOnlyList<TelemetryFrame> frames, double fps, ExternalTrack track)
	{
		if (frames.Count == 0 || fps <= 0) return null;

		double length = frames[^1].SampleTimeSeconds - frames[0].SampleTimeSeconds + 1 / fps;
		double tolerance = Math.Max(3, length * 0.02);
		List<ExternalTrackRecording> fitting = [.. track.Recordings.Where(r => Math.Abs((r.EndUtc - r.StartUtc).TotalSeconds - length) <= tolerance)];
		return fitting.Count == 1 ? fitting[0].StartUtc.AddSeconds(-frames[0].SampleTimeSeconds) : null;
	}

	/// <summary>
	///     A clip that is only a part of one recording span of the log (the drone splits a recording into files, the log keeps one span): the
	///     one span that holds the clip's start by the container's creation time. A clip starting at the span's start is placed on it exactly;
	///     a later one by its creation time, which is off by the two clocks' difference (a few seconds) - the anchor can be set by hand.
	/// </summary>
	public static DateTime? ContainingSpanAnchor(IReadOnlyList<TelemetryFrame> frames, double fps, ExternalTrack track, DateTime? recordingStartUtc)
	{
		if (recordingStartUtc is not { } start || frames.Count == 0 || fps <= 0) return null;

		double length = frames[^1].SampleTimeSeconds - frames[0].SampleTimeSeconds + 1 / fps;
		var slack = TimeSpan.FromSeconds(SpanClockSlackSeconds);
		List<ExternalTrackRecording> holding = [.. track.Recordings.Where(r => (r.EndUtc - r.StartUtc).TotalSeconds >= length && start >= r.StartUtc - slack && start <= r.EndUtc + slack)];
		if (holding.Count != 1) return null;

		DateTime clipStart = Math.Abs((start - holding[0].StartUtc).TotalSeconds) <= SpanClockSlackSeconds ? holding[0].StartUtc : start;
		return clipStart.AddSeconds(-frames[0].SampleTimeSeconds);
	}

	private const double SpanClockSlackSeconds = 10;

	public FileSummary Apply(FileSummary summary)
	{
		Validate();
		return Apply(summary, ExternalTrackReader.Read(Path));
	}

	/// <summary>Applies the track snapshot already read for this load operation, also used by the GUI's details.</summary>
	public FileSummary Apply(FileSummary summary, ExternalTrack track)
	{
		IReadOnlyList<TelemetryFrame> frames = ApplyCore(summary.TelemetryFrames ?? [], summary.Video.Fps, summary.ContainerRecordingStartUtc, track).Frames;
		List<DerivedFrame> derived = TelemetryProcessor.Process(frames, summary.CameraFormat);
		return summary with
		{
			TelemetryFrames = frames, DerivedFrames = derived, Telemetry = TelemetryProcessor.Summarize(derived), GpsClockShift = null,
			CameraModel = summary.CameraModel ?? track.DeviceName
		};
	}

	/// <summary>
	///     The frames with the track's position, altitude, speed and time in them (ExternalTrackMerge). `recordingStartUtc` is the
	///     container's creation time - the camera's clock at the first frame. Throws InvalidDataException for a file that isn't a
	///     track, InvalidOperationException when there is no way to tell where the recording lies on the track.
	/// </summary>
	public IReadOnlyList<TelemetryFrame> Apply(IReadOnlyList<TelemetryFrame> frames, double fps, DateTime? recordingStartUtc)
	{
		return ApplyCore(frames, fps, recordingStartUtc).Frames;
	}

	private (IReadOnlyList<TelemetryFrame> Frames, ExternalTrack Track) ApplyCore(IReadOnlyList<TelemetryFrame> frames, double fps, DateTime? recordingStartUtc, ExternalTrack? loadedTrack = null)
	{
		Validate();
		ExternalTrack track = loadedTrack ?? ExternalTrackReader.Read(Path);
		string name = System.IO.Path.GetFileName(Path);
		AppLogger.Info($"Track {name}: {track.Points.Count} fixes {track.StartUtc:HH:mm:ss}-{track.EndUtc:HH:mm:ss} UTC, " +
		               $"{track.Pauses.Count} stop(s) of the timer, {(track.Points.Any(p => p.SpeedMs is not null) ? "with" : "without")} speed");

		TrackSynchronization synchronization = Resolve(frames, fps, recordingStartUtc, track);
		if (frames.Count == 0 || synchronization.At(frames[0].SampleTimeSeconds) > track.EndUtc || synchronization.At(frames[^1].SampleTimeSeconds) < track.StartUtc)
			throw new InvalidOperationException(CoreStrings.ExternalTrack_NoOverlap);
		AppLogger.Info($"Track {name}: video start {synchronization.At(0):O}, clock rate {synchronization.Rate:0.#########}");
		return (ExternalTrackMerge.Apply(frames, track, synchronization), track);
	}
}
