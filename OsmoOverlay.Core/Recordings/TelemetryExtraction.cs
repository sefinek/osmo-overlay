using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

public static class TelemetryExtraction
{
	// Files the camera split one recording into follow each other within a frame by the GPS clock (measured
	// 0.1 s off at most); a file starting this much later than the previous one ended was recorded after a stop.
	private const double GapToleranceSeconds = 2.0;

	/// <summary>A file's telemetry, read by the camera that recorded it (SourceInfo.Camera).</summary>
	private static TelemetryExtractionResult Extract(string inputPath, SourceInfo source)
	{
		CameraRecording camera = source.Camera ?? throw new InvalidOperationException($"{Path.GetFileName(inputPath)} has no telemetry the app reads.");
		return camera.Format.ExtractTelemetry(inputPath, source);
	}

	/// <summary>
	///     Stitches telemetry from several consecutive files (a camera auto-splits long recordings,
	///     e.g. DJI Osmo Action) into one continuous stream: each segment's SampleTimeSeconds is shifted onto
	///     the combined timeline, so everything downstream (TelemetryProcessor's distance/speed/trail
	///     origin, OverlayRenderer's compass trail) sees one uninterrupted ride instead of resetting
	///     at every file boundary. GPS time becomes one clock running with the video (ApplyGpsClock).
	/// </summary>
	public static TelemetryExtractionResult ExtractCombined(IReadOnlyList<VideoSegment> segments)
	{
		return Combine(segments, segment => Extract(segment.InputPath, segment.Source));
	}

	internal static TelemetryExtractionResult Combine(IReadOnlyList<VideoSegment> segments, Func<VideoSegment, TelemetryExtractionResult> extract)
	{
		var combinedFrames = new List<TelemetryFrame>();
		string? cameraModel = null;
		TimeSpan? clockShift = null;
		(double Lat, double Lon, double AltitudeMeters)? lastKnownFix = null;
		(DateTime? GpsStart, double Offset)? previous = null;
		List<(int FirstFrame, double Offset, DateTime? ClockStart)> files = [];

		foreach (VideoSegment segment in segments)
		{
			TelemetryExtractionResult result = extract(segment);
			cameraModel ??= result.CameraModel;
			clockShift ??= result.GpsClockShift;
			if (result.Frames.Count == 0) continue;

			var offsetFrames = new List<TelemetryFrame>(result.Frames.Count);
			foreach (TelemetryFrame frame in result.Frames)
				offsetFrames.Add(frame with { SampleTimeSeconds = frame.SampleTimeSeconds + segment.StartOffsetSeconds });

			DateTime? gpsStart = GpsStartUtc(result.Frames);
			double? gap = previous is { } before ? GapSeconds(before.GpsStart, before.Offset, gpsStart, segment.StartOffsetSeconds) : null;
			bool afterGap = gap > GapToleranceSeconds;
			// A split goes on with the clock already running: each file's own start, read off the receiver's ticks, lands a
			// frame or two either way (measured -37 ms at a real join), which would step the time back at every split.
			bool clockRunning = files.Any(f => f.ClockStart is not null);
			files.Add((combinedFrames.Count, segment.StartOffsetSeconds, afterGap || !clockRunning ? gpsStart : null));
			if (afterGap)
			{
				offsetFrames[0] = offsetFrames[0] with { StartsAfterGap = true };
				AppLogger.Info($"{Path.GetFileName(segment.InputPath)} starts {gap:F1} s after the previous file ended (a stop, not a split) - " +
				               "speed, distance and the route don't run across it");
			}

			// After a stop the camera may be somewhere else - the previous file's last fix held into this one's
			// leading no-fix frames would add the jump to where its own fix lands to the distance and the route.
			if (!(afterGap && BackfillBeforeFirstFix(offsetFrames)) && lastKnownFix is { } fix)
				BridgeLeadingGpsGap(offsetFrames, fix);

			previous = (gpsStart, segment.StartOffsetSeconds);

			combinedFrames.AddRange(offsetFrames);

			TelemetryFrame last = offsetFrames[^1];
			if (!IsNullIsland(last))
				lastKnownFix = (last.Latitude, last.Longitude, last.AltitudeMeters);
		}

		BackfillBeforeFirstFix(combinedFrames);
		ApplyGpsClock(combinedFrames, files);
		return new TelemetryExtractionResult(combinedFrames, cameraModel, clockShift);
	}

	/// <summary>
	///     When a file's first frame was recorded, by the GPS clock: the first frame the GPS time (whole seconds) ticks
	///     over on is at that second, less the frame's own time in the file. Without a tick (a file shorter than a second of
	///     GPS time), the first timestamp less its frame's time - up to a second early. Null without any GPS time.
	/// </summary>
	internal static DateTime? GpsStartUtc(IReadOnlyList<TelemetryFrame> frames)
	{
		DateTime? last = null;
		DateTime? fallback = null;
		foreach (TelemetryFrame frame in frames)
		{
			if (frame.GpsTimestamp is not { } timestamp) continue;
			if (last is { } seen && timestamp != seen) return timestamp.AddSeconds(-frame.SampleTimeSeconds);
			last = timestamp;
			fallback ??= timestamp.AddSeconds(-frame.SampleTimeSeconds);
		}

		return fallback;
	}

	/// <summary>
	///     Every frame's GpsTimestamp becomes a clock running with the video instead of the receiver's whole seconds, which
	///     start only once it has the time and tick over a fraction of a second off the frames: a file's ClockStart (its
	///     GpsStartUtc - set for the first file with GPS time and for each one after a stop) plus the frame's time from
	///     there. A file without one continues the clock before it, or at the start is counted back from the first one -
	///     files without GPS time can't tell a split from a stop, so they stay joined like everything else
	///     (ExtractCombined). Frames before the GPS had the time get it counted back too. No GPS time in any file leaves
	///     every timestamp null.
	/// </summary>
	private static void ApplyGpsClock(List<TelemetryFrame> frames, List<(int FirstFrame, double Offset, DateTime? ClockStart)> files)
	{
		int anchor = files.FindIndex(f => f.ClockStart is not null);
		if (anchor < 0) return;

		DateTime known = files[anchor].ClockStart!.Value;
		double knownOffset = files[anchor].Offset;
		for (int i = 0; i < files.Count; i++)
		{
			if (i > anchor && files[i].ClockStart is { } start)
			{
				known = start;
				knownOffset = files[i].Offset;
			}

			DateTime fileStart = DateTime.SpecifyKind(files[i].ClockStart ?? known.AddSeconds(files[i].Offset - knownOffset), DateTimeKind.Utc);
			int end = i + 1 < files.Count ? files[i + 1].FirstFrame : frames.Count;
			for (int f = files[i].FirstFrame; f < end; f++)
				frames[f] = frames[f] with { GpsTimestamp = fileStart.AddSeconds(frames[f].SampleTimeSeconds - files[i].Offset) };
		}
	}

	/// <summary>How much later a file started than the previous one ended, in seconds - null when either has no GPS time.</summary>
	internal static double? GapSeconds(DateTime? previousStart, double previousOffset, DateTime? start, double offset)
	{
		return previousStart is { } before && start is { } after ? (after - before).TotalSeconds - (offset - previousOffset) : null;
	}

	/// <summary>
	///     A recording that starts before the GPS receiver has its first fix (the camera was just powered
	///     on, or started indoors) otherwise keeps the (0,0) no-fix-yet sentinel (TelemetryFrame.HasGpsFix) for that whole leading
	///     run - and TelemetryProcessor would then take Null Island as the route origin, add the (0,0) to
	///     first-fix hop to the cumulative distance, and hand the map/route-intro a bounding box spanning
	///     half the globe. Holding the first real fix backwards instead is the same "hold, don't snap to
	///     (0,0)" policy the camera format applies going forward. HasGpsFix stays false on those frames, so
	///     FindGpsLossRanges still reports them. No-op for a recording with no fix at all - false then.
	/// </summary>
	private static bool BackfillBeforeFirstFix(List<TelemetryFrame> frames)
	{
		int firstFixIndex = frames.FindIndex(f => !IsNullIsland(f));
		if (firstFixIndex < 0) return false;

		if (firstFixIndex > 0)
		{
			BridgeLeadingGpsGap(frames,
				(frames[firstFixIndex].Latitude, frames[firstFixIndex].Longitude, frames[firstFixIndex].AltitudeMeters));
		}

		return true;
	}

	private static bool IsNullIsland(TelemetryFrame frame)
	{
		return frame.Latitude == 0 && frame.Longitude == 0;
	}

	/// <summary>
	///     Each segment's own extraction starts at (0,0) until that
	///     file's first real fix arrives - across a segment boundary that reads as a spurious jump to
	///     null island. Carry the previous segment's last known position into those leading frames
	///     instead, matching the same (0,0)-means-no-fix-yet sentinel the camera formats use.
	/// </summary>
	private static void BridgeLeadingGpsGap(List<TelemetryFrame> frames,
		(double Lat, double Lon, double AltitudeMeters) lastKnownFix)
	{
		for (int i = 0; i < frames.Count; i++)
		{
			TelemetryFrame frame = frames[i];
			if (!IsNullIsland(frame)) break;

			frames[i] = frame with
			{
				Latitude = lastKnownFix.Lat, Longitude = lastKnownFix.Lon, AltitudeMeters = lastKnownFix.AltitudeMeters
			};
		}
	}
}
