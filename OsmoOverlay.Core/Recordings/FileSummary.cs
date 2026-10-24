using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

public sealed record FileSummary(
	IReadOnlyList<string> InputPaths,
	// Each file's container duration (where its audio ends - what the audio is joined by) and its video frames.
	IReadOnlyList<double> SegmentDurationsSeconds,
	IReadOnlyList<long> SegmentFrameCounts,
	string? CameraModel,
	VideoInfo Video,
	AudioInfo? Audio,
	double DurationSeconds,
	long FileSizeBytes,
	bool HasTelemetry,
	IReadOnlyList<TelemetryFrame>? TelemetryFrames,
	IReadOnlyList<DerivedFrame>? DerivedFrames,
	TelemetrySummary? Telemetry,
	long TotalFrameCount,
	// See SourceInfo.ContainerCreationTimeUtc - the fallback DateTimeText/UtcTimeText use when this
	// recording has no GPS timestamp anywhere (OverlayRenderer.DrawTimeText).
	DateTime? ContainerRecordingStartUtc = null,
	bool FromCache = false,
	// The camera that recorded it (ICameraFormat.Id) - what reads its telemetry's axes (tilt, leveling).
	string? CameraFormatId = null,
	// A 360 recording's lenses (SourceInfo.Fisheye) - Video is then the flat picture reframed from them.
	DualFisheye? Fisheye = null,
	// How far the camera's GPS time was moved to match its own clock (TelemetryExtractionResult.GpsClockShift).
	TimeSpan? GpsClockShift = null)
{
	/// <summary>The registered camera format this recording came from - null when none of them knows it.</summary>
	[JsonIgnore]
	public ICameraFormat? CameraFormat => CameraFormats.Find(CameraFormatId);
}

public enum FileSummaryReadStage
{
	Cache,
	Probe,
	Extract,
	Process,
	CameraModel,
	Save
}

public static class FileSummaryReader
{
	/// <summary>The current on-disk cache format - bumped whenever telemetry extraction/derivation logic changes (see FileSummaryCache.FormatVersion).</summary>
	public static int CurrentCacheFormatVersion => FileSummaryCache.FormatVersion;

	public static int ClearCache()
	{
		return FileSummaryCache.ClearAll();
	}

	/// <summary>Deletes cached summaries and waveforms no recording has used for `unusedFor` - how many files went.</summary>
	public static int DeleteUnusedCache(TimeSpan unusedFor)
	{
		return FileSummaryCache.DeleteUnused(unusedFor);
	}

	public static FileSummary Read(string inputPath)
	{
		return Read([inputPath]).Summary!;
	}

	/// <summary>
	///     The files' summary, or - Summary null - why they can't be read as one recording (VideoSegments.FindMismatch).
	///     onCacheEvent fires synchronously on the calling thread: once right after the cache lookup (before
	///     any probing/extraction starts, so a caller can say what's about to happen rather than after the
	///     fact), and once more after a recompute with whether it made it into the cache.
	/// </summary>
	public static (FileSummary? Summary, string? Problem) Read(IReadOnlyList<string> inputPaths,
		Action<FileSummaryCacheEvent>? onCacheEvent = null, Action<FileSummaryReadStage>? onProgress = null)
	{
		var stopwatch = Stopwatch.StartNew();
		onProgress?.Invoke(FileSummaryReadStage.Cache);
		(FileSummary? cached, int? staleFormatVersion, long staleSizeBytes) = FileSummaryCache.TryLoad(inputPaths);
		if (cached is not null)
		{
			AppLogger.Info($"Summary: cache hit in {stopwatch.ElapsedMilliseconds} ms ({inputPaths.Count} file(s))");
			onCacheEvent?.Invoke(new FileSummaryCacheEvent(FileSummaryCacheEventKind.Hit, CurrentCacheFormatVersion));
			return (cached with { FromCache = true }, null);
		}

		onCacheEvent?.Invoke(staleFormatVersion is { } previous
			? new FileSummaryCacheEvent(FileSummaryCacheEventKind.Stale, CurrentCacheFormatVersion, previous, staleSizeBytes)
			: new FileSummaryCacheEvent(FileSummaryCacheEventKind.Miss, CurrentCacheFormatVersion));

		long cacheMs = stopwatch.ElapsedMilliseconds;
		onProgress?.Invoke(FileSummaryReadStage.Probe);
		IReadOnlyList<VideoSegment> segments = VideoSegments.ProbeAll(inputPaths);
		long probeMs = stopwatch.ElapsedMilliseconds - cacheMs;
		long extractMs = 0, processMs = 0;
		if (VideoSegments.FindMismatch(segments) is { } mismatch) return (null, mismatch);
		VideoSegment first = segments[0];
		long fileSize = inputPaths.Sum(path => new FileInfo(path).Length);
		double durationSeconds = segments.TotalDurationSeconds();
		var segmentDurations = segments.Select(s => s.Source.DurationSeconds).ToList();
		var segmentFrames = segments.Select(s => s.FrameCount()).ToList();
		long totalFrameCount = segments.TotalFrameCount();

		FileSummary summary;
		if (!segments.AllHaveTelemetry())
		{
			onProgress?.Invoke(FileSummaryReadStage.CameraModel);
			string? cameraModelOnly = TryGetCameraModel(inputPaths[0]);
			summary = new FileSummary(inputPaths, segmentDurations, segmentFrames, cameraModelOnly, first.Source.Video,
				first.Source.Audio, durationSeconds, fileSize, false, null, null, null, totalFrameCount,
				first.Source.ContainerCreationTimeUtc, CameraFormatId: first.Source.Camera?.Format.Id, Fisheye: first.Source.Fisheye);
		}
		else
		{
			ICameraFormat camera = first.Source.Camera!.Format;
			onProgress?.Invoke(FileSummaryReadStage.Extract);
			long stageStart = stopwatch.ElapsedMilliseconds;
			TelemetryExtractionResult extraction = TelemetryExtraction.ExtractCombined(segments);
			extractMs = stopwatch.ElapsedMilliseconds - stageStart;
			stageStart = stopwatch.ElapsedMilliseconds;
			List<DerivedFrame>? derivedFrames = null;
			TelemetrySummary? telemetry = null;
			if (extraction.Frames.Count > 0)
			{
				onProgress?.Invoke(FileSummaryReadStage.Process);
				derivedFrames = TelemetryProcessor.Process(extraction.Frames, camera);
				telemetry = TelemetryProcessor.Summarize(derivedFrames);
			}

			processMs = stopwatch.ElapsedMilliseconds - stageStart;

			if (extraction.CameraModel is null) onProgress?.Invoke(FileSummaryReadStage.CameraModel);
			string? cameraModel = extraction.CameraModel ?? TryGetCameraModel(inputPaths[0]);
			summary = new FileSummary(inputPaths, segmentDurations, segmentFrames, cameraModel, first.Source.Video,
				first.Source.Audio, durationSeconds, fileSize, true, extraction.Frames, derivedFrames, telemetry,
				totalFrameCount, first.Source.ContainerCreationTimeUtc, CameraFormatId: camera.Id, Fisheye: first.Source.Fisheye,
				GpsClockShift: extraction.GpsClockShift);
		}

		onProgress?.Invoke(FileSummaryReadStage.Save);
		long saveStart = stopwatch.ElapsedMilliseconds;
		bool saved = FileSummaryCache.Save(inputPaths, summary);
		AppLogger.Info($"Summary: cache lookup {cacheMs} ms, probe {probeMs} ms, extraction {extractMs} ms, processing {processMs} ms, " +
		               $"cache save {stopwatch.ElapsedMilliseconds - saveStart} ms, {stopwatch.ElapsedMilliseconds} ms in all ({inputPaths.Count} file(s))");
		onCacheEvent?.Invoke(new FileSummaryCacheEvent(
			saved ? FileSummaryCacheEventKind.Saved : FileSummaryCacheEventKind.SaveFailed, CurrentCacheFormatVersion));
		return (summary, null);
	}

	/// <summary>exiftool is optional - without it the camera model is just unknown, not a failed summary.</summary>
	private static string? TryGetCameraModel(string path)
	{
		try
		{
			return ExifToolRunner.GetCameraModel(path);
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
		{
			AppLogger.Info($"Camera model not read (exiftool unavailable: {ex.Message})");
			return null;
		}
	}
}
