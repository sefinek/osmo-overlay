using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

public static class TelemetryExtraction
{
	// Files the camera split one recording into follow each other within a frame by the GPS clock (measured
	// 0.1 s off at most); a file starting this much later than the previous one ended was recorded after a stop.
	private const double GapToleranceSeconds = 2.0;

	public static TelemetryExtractionResult Extract(string inputPath, SourceInfo source)
	{
		if (source.DjmdStreamIndex is { } djmdStreamIndex)
			try
			{
				ReadOnlyMemory<byte> raw = DjiMetaTelemetryParser.ExtractRawStream(inputPath, djmdStreamIndex);
				return DjiMetaTelemetryParser.Parse(raw, source.Video.Fps);
			}
			catch (Exception ex)
			{
				// Native djmd decode is verified against DJI Osmo Action 6 firmware; fall back
				// to exiftool below for other models/firmware where the raw layout might differ.
				AppLogger.Warn(ex, $"Native djmd decode failed for {inputPath}, falling back to exiftool");
			}

		return ExifToolRunner.Extract(inputPath);
	}

	/// <summary>
	///     Stitches telemetry from several consecutive files (DJI Osmo Action auto-splits long
	///     recordings) into one continuous stream: each segment's SampleTimeSeconds is shifted onto
	///     the combined timeline, so everything downstream (TelemetryProcessor's distance/speed/trail
	///     origin, OverlayRenderer's compass trail) sees one uninterrupted ride instead of resetting
	///     at every file boundary.
	/// </summary>
	public static TelemetryExtractionResult ExtractCombined(IReadOnlyList<VideoSegment> segments)
	{
		var combinedFrames = new List<TelemetryFrame>();
		string? cameraModel = null;
		(double Lat, double Lon, double AltitudeMeters)? lastKnownFix = null;
		(DateTime? GpsStart, double Offset)? previous = null;

		foreach (VideoSegment segment in segments)
		{
			TelemetryExtractionResult result = Extract(segment.InputPath, segment.Source);
			cameraModel ??= result.CameraModel;
			if (result.Frames.Count == 0) continue;

			var offsetFrames = new List<TelemetryFrame>(result.Frames.Count);
			foreach (TelemetryFrame frame in result.Frames)
				offsetFrames.Add(frame with { SampleTimeSeconds = frame.SampleTimeSeconds + segment.StartOffsetSeconds });

			if (lastKnownFix is { } fix)
				BridgeLeadingGpsGap(offsetFrames, fix);

			DateTime? gpsStart = GpsStartUtc(result.Frames);
			if (previous is { } before && GapSeconds(before.GpsStart, before.Offset, gpsStart, segment.StartOffsetSeconds) is { } gap &&
			    gap > GapToleranceSeconds)
			{
				offsetFrames[0] = offsetFrames[0] with { StartsAfterGap = true };
				AppLogger.Info($"{Path.GetFileName(segment.InputPath)} starts {gap:F1} s after the previous file ended (a stop, not a split) - " +
				               "speed, distance and the route don't run across it");
			}

			previous = (gpsStart, segment.StartOffsetSeconds);

			combinedFrames.AddRange(offsetFrames);

			TelemetryFrame last = offsetFrames[^1];
			if (!IsNullIsland(last))
				lastKnownFix = (last.Latitude, last.Longitude, last.AltitudeMeters);
		}

		BackfillBeforeFirstFix(combinedFrames);
		return new TelemetryExtractionResult(combinedFrames, cameraModel);
	}

	/// <summary>
	///     When a file's first frame was recorded, by the GPS clock: the first frame the GPS time (whole seconds) ticks
	///     over on is at that second, less the frame's own time in the file. Null without a tick (no GPS time).
	/// </summary>
	internal static DateTime? GpsStartUtc(IReadOnlyList<TelemetryFrame> frames)
	{
		DateTime? last = null;
		foreach (TelemetryFrame frame in frames)
		{
			if (frame.GpsTimestamp is not { } timestamp) continue;
			if (last is { } seen && timestamp != seen) return timestamp.AddSeconds(-frame.SampleTimeSeconds);
			last = timestamp;
		}

		return null;
	}

	/// <summary>How much later a file started than the previous one ended, in seconds - null when either has no GPS time.</summary>
	internal static double? GapSeconds(DateTime? previousStart, double previousOffset, DateTime? start, double offset)
	{
		return previousStart is { } before && start is { } after ? (after - before).TotalSeconds - (offset - previousOffset) : null;
	}

	/// <summary>
	///     A recording that starts before the GPS receiver has its first fix (the camera was just powered
	///     on, or started indoors) otherwise keeps GpsForwardFill's (0,0) sentinel for that whole leading
	///     run - and TelemetryProcessor would then take Null Island as the route origin, add the (0,0) to
	///     first-fix hop to the cumulative distance, and hand the map/route-intro a bounding box spanning
	///     half the globe. Holding the first real fix backwards instead is the same "hold, don't snap to
	///     (0,0)" policy GpsForwardFill applies going forward. HasGpsFix stays false on those frames, so
	///     FindGpsLossRanges still reports them. No-op for a recording with no fix at all.
	/// </summary>
	private static void BackfillBeforeFirstFix(List<TelemetryFrame> frames)
	{
		var firstFixIndex = frames.FindIndex(f => !IsNullIsland(f));
		if (firstFixIndex <= 0) return;

		BridgeLeadingGpsGap(frames,
			(frames[firstFixIndex].Latitude, frames[firstFixIndex].Longitude, frames[firstFixIndex].AltitudeMeters));
	}

	private static bool IsNullIsland(TelemetryFrame frame)
	{
		return frame.Latitude == 0 && frame.Longitude == 0;
	}

	/// <summary>
	///     A fresh GpsForwardFill inside each segment's own extraction starts at (0,0) until that
	///     file's first real fix arrives - across a segment boundary that reads as a spurious jump to
	///     null island. Carry the previous segment's last known position into those leading frames
	///     instead, matching the same (0,0)-means-no-fix-yet sentinel GpsForwardFill already uses.
	/// </summary>
	private static void BridgeLeadingGpsGap(List<TelemetryFrame> frames,
		(double Lat, double Lon, double AltitudeMeters) lastKnownFix)
	{
		for (var i = 0; i < frames.Count; i++)
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
