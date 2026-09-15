using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

public static class TelemetryExtraction
{
	public static TelemetryExtractionResult Extract(string inputPath, SourceInfo source)
	{
		if (source.DjmdStreamIndex is { } djmdStreamIndex)
			try
			{
				var raw = DjiMetaTelemetryParser.ExtractRawStream(inputPath, djmdStreamIndex);
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

		foreach (VideoSegment segment in segments)
		{
			TelemetryExtractionResult result = Extract(segment.InputPath, segment.Source);
			cameraModel ??= result.CameraModel;

			var offsetFrames = new List<TelemetryFrame>(result.Frames.Count);
			foreach (TelemetryFrame frame in result.Frames)
				offsetFrames.Add(frame with { SampleTimeSeconds = frame.SampleTimeSeconds + segment.StartOffsetSeconds });

			if (lastKnownFix is { } fix)
				BridgeLeadingGpsGap(offsetFrames, fix);

			combinedFrames.AddRange(offsetFrames);

			TelemetryFrame last = offsetFrames[^1];
			if (last.Latitude != 0 || last.Longitude != 0)
				lastKnownFix = (last.Latitude, last.Longitude, last.AltitudeMeters);
		}

		return new TelemetryExtractionResult(combinedFrames, cameraModel);
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
			if (frame.Latitude != 0 || frame.Longitude != 0) break;

			frames[i] = frame with
			{
				Latitude = lastKnownFix.Lat, Longitude = lastKnownFix.Lon, AltitudeMeters = lastKnownFix.AltitudeMeters
			};
		}
	}
}
