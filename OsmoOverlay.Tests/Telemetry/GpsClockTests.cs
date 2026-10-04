using OsmoOverlay.Core;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Telemetry;

/// <summary>The receiver's whole GPS seconds turned into one clock per file running with the video (TelemetryExtraction.Combine).</summary>
[TestClass]
public sealed class GpsClockTests
{
	private const double Hz = 10;
	private static readonly DateTime Start = new(2026, 9, 30, 13, 27, 43, 400, DateTimeKind.Utc);

	/// <summary>`frames` samples at Hz; GPS time (whole seconds, as the receiver gives it) from sample `gpsFrom` on, the file starting at `fileStart`.</summary>
	private static List<TelemetryFrame> Recording(int frames, DateTime fileStart, int? gpsFrom)
	{
		return
		[
			.. Enumerable.Range(0, frames).Select(i =>
			{
				DateTime clock = fileStart.AddSeconds(i / Hz);
				DateTime? gps = i >= gpsFrom ? DateTime.SpecifyKind(clock.AddTicks(-(clock.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Unspecified) : null;
				return new TelemetryFrame(i, i / Hz, 50, 20, 200, gps, 0, 0, 1);
			})
		];
	}

	private static TelemetryExtractionResult Combine(params List<TelemetryFrame>[] files)
	{
		var video = new VideoInfo("hevc", "Main 10", 3840, 2160, "10", "yuv420p10le", null, null, null, null, 90_000_000);
		List<SourceInfo> sources = [.. files.Select(f => new SourceInfo(video with { FrameCount = f.Count }, null, f.Count / Hz + 0.03, null))];
		IReadOnlyList<VideoSegment> segments = VideoSegments.Join([.. files.Select((_, i) => $"{i}.mp4")], sources);
		return TelemetryExtraction.Combine(segments, segment => new TelemetryExtractionResult(files[int.Parse(Path.GetFileNameWithoutExtension(segment.InputPath))], null));
	}

	[TestMethod]
	public void EveryFrame_GetsTheGpsClock_CountedBackToTheFirstFrame()
	{
		List<TelemetryFrame> frames = Combine(Recording(60, Start, 25)).Frames;

		for (int i = 0; i < frames.Count; i++)
			Assert.AreEqual(0, (frames[i].GpsTimestamp!.Value - Start.AddSeconds(i / Hz)).TotalSeconds, 0.1 + 1e-9, $"frame {i}");
		Assert.AreEqual(DateTimeKind.Utc, frames[0].GpsTimestamp!.Value.Kind);
	}

	[TestMethod]
	public void AFileWithoutGpsTime_ContinuesTheOneBefore_OrIsCountedBackFromTheNext()
	{
		List<TelemetryFrame> before = Combine(Recording(30, Start, 0), Recording(30, Start.AddSeconds(3), null)).Frames;
		Assert.AreEqual(0, (before[45].GpsTimestamp!.Value - Start.AddSeconds(4.5)).TotalSeconds, 0.1 + 1e-9);

		List<TelemetryFrame> after = Combine(Recording(30, Start, null), Recording(30, Start.AddSeconds(3), 0)).Frames;
		Assert.AreEqual(0, (after[0].GpsTimestamp!.Value - Start).TotalSeconds, 0.1 + 1e-9);
	}

	[TestMethod]
	public void ASplit_GoesOnWithTheSameClock_ThoughItsOwnTicksLandAFrameOff()
	{
		List<TelemetryFrame> frames = Combine(Recording(30, Start, 0), Recording(30, Start.AddSeconds(3.05), 0)).Frames;

		Assert.IsFalse(frames[30].StartsAfterGap);
		Assert.AreEqual(1 / Hz, (frames[30].GpsTimestamp!.Value - frames[29].GpsTimestamp!.Value).TotalSeconds, 1e-6);
	}

	[TestMethod]
	public void AFileAfterAStop_KeepsItsOwnClock()
	{
		List<TelemetryFrame> frames = Combine(Recording(30, Start, 0), Recording(30, Start.AddMinutes(5), 0)).Frames;

		Assert.IsTrue(frames[30].StartsAfterGap);
		Assert.AreEqual(0, (frames[30].GpsTimestamp!.Value - Start.AddMinutes(5)).TotalSeconds, 0.1 + 1e-9);
	}

	[TestMethod]
	public void NoGpsTimeAnywhere_StaysUnknown()
	{
		Assert.IsTrue(Combine(Recording(30, Start, null), Recording(30, Start, null)).Frames.All(f => f.GpsTimestamp is null));
	}
}
