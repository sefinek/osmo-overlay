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
	// See SourceInfo.ContainerCreationTimeUtc - the fallback DateTimeText/UtcTimeText use when this
	// recording has no GPS timestamp anywhere (OverlayRenderer.DrawTimeText).
	DateTime? ContainerRecordingStartUtc = null,
	bool FromCache = false,
	// Transient, reporting-only (see FileSummaryCache.TryLoad) - never what gets persisted back to
	// the cache file, only set on the FileSummary instance handed back to this particular caller.
	int? StaleCacheFormatVersion = null);

public static class FileSummaryReader
{
	/// <summary>The current on-disk cache format - bumped whenever telemetry extraction/derivation logic changes (see FileSummaryCache.FormatVersion).</summary>
	public static int CurrentCacheFormatVersion => FileSummaryCache.FormatVersion;

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
		(FileSummary? cached, var staleFormatVersion) = FileSummaryCache.TryLoad(inputPaths);
		if (cached is not null)
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
				first.Source.Audio, durationSeconds, fileSize, false, null, null, null,
				first.Source.ContainerCreationTimeUtc);
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
				first.Source.Audio, durationSeconds, fileSize, true, extraction.Frames, derivedFrames, telemetry,
				first.Source.ContainerCreationTimeUtc);
		}

		FileSummaryCache.Save(inputPaths, summary);
		return summary with { StaleCacheFormatVersion = staleFormatVersion };
	}
}
