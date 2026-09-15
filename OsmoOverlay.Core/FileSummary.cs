using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

public sealed record FileSummary(
	IReadOnlyList<string> InputPaths,
	IReadOnlyList<double> SegmentDurationsSeconds,
	string? CameraModel,
	VideoInfo Video,
	AudioInfo? Audio,
	double DurationSeconds,
	long FileSizeBytes,
	bool HasTelemetry,
	IReadOnlyList<TelemetryFrame>? TelemetryFrames,
	IReadOnlyList<DerivedFrame>? DerivedFrames,
	TelemetrySummary? Telemetry,
	bool FromCache = false);

public static class FileSummaryReader
{
	public static int ClearCache()
	{
		return FileSummaryCache.ClearAll();
	}

	public static FileSummary Read(string inputPath)
	{
		return Read([inputPath]);
	}

	public static FileSummary Read(IReadOnlyList<string> inputPaths)
	{
		if (FileSummaryCache.TryLoad(inputPaths) is { } cached)
			return cached with { FromCache = true };

		IReadOnlyList<VideoSegment> segments = VideoSegments.ProbeAll(inputPaths);
		VideoSegments.Validate(segments);
		VideoSegment first = segments[0];
		var fileSize = inputPaths.Sum(path => new FileInfo(path).Length);
		var durationSeconds = segments.TotalDurationSeconds();
		List<double> segmentDurations = segments.Select(s => s.Source.DurationSeconds).ToList();

		FileSummary summary;
		if (!segments.AllHaveDjmdTrack())
		{
			var cameraModelOnly = ExifToolRunner.GetCameraModel(inputPaths[0]);
			summary = new FileSummary(inputPaths, segmentDurations, cameraModelOnly, first.Source.Video,
				first.Source.Audio, durationSeconds, fileSize, false, null, null, null);
		}
		else
		{
			TelemetryExtractionResult extraction = TelemetryExtraction.ExtractCombined(segments);
			List<DerivedFrame>? derivedFrames = null;
			TelemetrySummary? telemetry = null;
			if (extraction.Frames.Count > 0)
			{
				derivedFrames = TelemetryProcessor.Process(extraction.Frames);
				telemetry = TelemetryProcessor.Summarize(derivedFrames);
			}

			var cameraModel = extraction.CameraModel ?? ExifToolRunner.GetCameraModel(inputPaths[0]);
			summary = new FileSummary(inputPaths, segmentDurations, cameraModel, first.Source.Video,
				first.Source.Audio, durationSeconds, fileSize, true, extraction.Frames, derivedFrames, telemetry);
		}

		FileSummaryCache.Save(inputPaths, summary);
		return summary;
	}
}
