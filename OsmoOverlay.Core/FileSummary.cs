using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

public sealed record FileSummary(
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
	public const int CacheFormatVersion = FileSummaryCache.FormatVersion;

	public static string GetCachePath(string inputPath)
	{
		return FileSummaryCache.GetCachePath(inputPath);
	}

	public static FileSummary Read(string inputPath)
	{
		if (FileSummaryCache.TryLoad(inputPath) is { } cached)
			return cached with { FromCache = true };

		SourceInfo source = SourceProbe.Probe(inputPath);
		var fileSize = new FileInfo(inputPath).Length;

		string? cameraModel = null;
		List<TelemetryFrame>? telemetryFrames = null;
		List<DerivedFrame>? derivedFrames = null;
		TelemetrySummary? telemetry = null;

		if (source.HasDjmdTrack)
		{
			TelemetryExtractionResult extraction = TelemetryExtraction.Extract(inputPath, source);
			telemetryFrames = extraction.Frames;
			cameraModel = extraction.CameraModel;

			if (telemetryFrames.Count > 0)
			{
				derivedFrames = TelemetryProcessor.Process(telemetryFrames);
				telemetry = TelemetryProcessor.Summarize(derivedFrames);
			}
		}

		cameraModel ??= ExifToolRunner.GetCameraModel(inputPath);

		var summary = new FileSummary(cameraModel, source.Video, source.Audio, source.DurationSeconds, fileSize,
			source.HasDjmdTrack, telemetryFrames, derivedFrames, telemetry);

		FileSummaryCache.Save(inputPath, summary);
		return summary;
	}
}
