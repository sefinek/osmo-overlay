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
	// Null only on a cache entry written before this existed - callers fall back to duration * fps.
	long? TotalFrameCount = null);

public enum FileSummaryCacheEventKind
{
	/// <summary>Valid entry found - nothing gets recomputed.</summary>
	Hit,
	/// <summary>No usable entry (never analyzed, file changed since, or unreadable) - recomputing.</summary>
	Miss,
	/// <summary>Entry for this exact file, but written by an older FormatVersion - deleted, recomputing.</summary>
	Stale,
	/// <summary>The recomputed summary was written to the cache.</summary>
	Saved,
	/// <summary>The recomputed summary couldn't be written (details in the log) - the next run recomputes again.</summary>
	SaveFailed
}

/// <summary>PreviousFormatVersion is set only for Stale - the version the deleted entry was written with.</summary>
public sealed record FileSummaryCacheEvent(FileSummaryCacheEventKind Kind, int CurrentFormatVersion, int? PreviousFormatVersion = null);

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

	/// <summary>
	///     onCacheEvent fires synchronously on the calling thread: once right after the cache lookup (before
	///     any probing/extraction starts, so a caller can say what's about to happen rather than after the
	///     fact), and once more after a recompute with whether it made it into the cache.
	/// </summary>
	public static FileSummary Read(IReadOnlyList<string> inputPaths, Action<FileSummaryCacheEvent>? onCacheEvent = null)
	{
		(FileSummary? cached, var staleFormatVersion) = FileSummaryCache.TryLoad(inputPaths);
		if (cached is not null)
		{
			onCacheEvent?.Invoke(new FileSummaryCacheEvent(FileSummaryCacheEventKind.Hit, CurrentCacheFormatVersion));
			return cached with { FromCache = true };
		}

		onCacheEvent?.Invoke(staleFormatVersion is { } previous
			? new FileSummaryCacheEvent(FileSummaryCacheEventKind.Stale, CurrentCacheFormatVersion, previous)
			: new FileSummaryCacheEvent(FileSummaryCacheEventKind.Miss, CurrentCacheFormatVersion));

		IReadOnlyList<VideoSegment> segments = VideoSegments.ProbeAll(inputPaths);
		VideoSegments.Validate(segments);
		VideoSegment first = segments[0];
		var fileSize = inputPaths.Sum(path => new FileInfo(path).Length);
		var durationSeconds = segments.TotalDurationSeconds();
		List<double> segmentDurations = segments.Select(s => s.Source.DurationSeconds).ToList();
		var totalFrameCount = segments.TotalFrameCount();

		FileSummary summary;
		if (!segments.AllHaveDjmdTrack())
		{
			var cameraModelOnly = ExifToolRunner.GetCameraModel(inputPaths[0]);
			summary = new FileSummary(inputPaths, segmentDurations, cameraModelOnly, first.Source.Video,
				first.Source.Audio, durationSeconds, fileSize, false, null, null, null,
				first.Source.ContainerCreationTimeUtc, TotalFrameCount: totalFrameCount);
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
				first.Source.ContainerCreationTimeUtc, TotalFrameCount: totalFrameCount);
		}

		var saved = FileSummaryCache.Save(inputPaths, summary);
		onCacheEvent?.Invoke(new FileSummaryCacheEvent(
			saved ? FileSummaryCacheEventKind.Saved : FileSummaryCacheEventKind.SaveFailed, CurrentCacheFormatVersion));
		return summary;
	}
}
