using OsmoOverlay.Core;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Telemetry;

/// <summary>Two files the camera recorded with a stop between them, joined on one timeline.</summary>
[TestClass]
public sealed class RecordingGapTests
{
	private const double Hz = 10;
	private const double MetersPerDegreeLat = 6_371_000 * Math.PI / 180;

	/// <summary>
	///     10 s northwards at 10 m/s, then a second file starting 1 km further north (ridden while the camera was off)
	///     for another 10 s, with no GPS-measured speed so speed comes from the positions.
	/// </summary>
	private static List<TelemetryFrame> Joined(bool markGap)
	{
		List<TelemetryFrame> frames = [];
		for (var i = 0; i < 200; i++)
		{
			var t = i / Hz;
			var north = i < 100 ? 10 * t : 1000 + 10 * (t - 10);
			frames.Add(new TelemetryFrame(i, t, 50 + north / MetersPerDegreeLat, 20, 200, null, 0, 0, 1,
				StartsAfterGap: markGap && i == 100));
		}

		return frames;
	}

	[TestMethod]
	public void Distance_DoesNotCountWhatWasRiddenBetweenTheFiles()
	{
		List<DerivedFrame> derived = TelemetryProcessor.Process(Joined(true));

		Assert.AreEqual(198, derived[^1].CumulativeDistanceMeters, 0.01);
		Assert.AreEqual(1099, TelemetryProcessor.Process(Joined(false))[^1].CumulativeDistanceMeters, 0.01);
	}

	[TestMethod]
	public void Speed_DoesNotSpanTheGap()
	{
		List<DerivedFrame> derived = TelemetryProcessor.Process(Joined(true));

		Assert.IsTrue(derived.Max(f => f.SpeedKmh) < 37, $"max {derived.Max(f => f.SpeedKmh):F1} km/h");
		Assert.AreEqual(36, derived[150].SpeedKmh, 0.5);
	}

	[TestMethod]
	public void FirstFrameAfterTheGap_IsMarkedForTheRoute_AndKeptThroughACut()
	{
		List<DerivedFrame> derived = TelemetryProcessor.Process(Joined(true));
		Assert.IsTrue(derived[100].StartsAfterCut);
		Assert.AreEqual(1, derived.Count(f => f.StartsAfterCut));

		var timeline = new OutputTimeline(RenderPlan.Resolve(null, null, [new TimeRange(2, 4)], null, Hz, 200), Hz);
		List<DerivedFrame> mapped = timeline.MapFrames(derived);
		Assert.AreEqual(2, mapped.Count(f => f.StartsAfterCut));
		Assert.IsTrue(mapped.Single(f => f.Raw.SourceTimeSeconds is 10.0).StartsAfterCut);
	}

	[TestMethod]
	public void GpsSmoothing_DoesNotInterpolateAcrossTheGap()
	{
		List<TelemetryFrame> frames = Joined(true);
		// A GPS that reports twice a second: every other sample repeats the last fix, as the camera's does.
		for (var i = 1; i < frames.Count; i += 2)
			if (!frames[i].StartsAfterGap)
				frames[i] = frames[i] with { Latitude = frames[i - 1].Latitude };

		List<DerivedFrame> derived = TelemetryProcessor.Process(frames, true);

		Assert.AreEqual(frames[99].Latitude, derived[99].Raw.Latitude);
	}

	[TestMethod]
	public void GpsStart_IsTheFirstTickLessItsTimeInTheFile()
	{
		var start = new DateTime(2026, 9, 14, 7, 8, 10, 600);
		List<TelemetryFrame> frames =
		[
			.. Enumerable.Range(0, 30).Select(i =>
			{
				var t = i / Hz;
				DateTime clock = start.AddSeconds(t);
				return new TelemetryFrame(i, t, 50, 20, 200, clock.AddTicks(-(clock.Ticks % TimeSpan.TicksPerSecond)), 0, 0, 1);
			})
		];

		DateTime? gpsStart = TelemetryExtraction.GpsStartUtc(frames);

		Assert.IsNotNull(gpsStart);
		Assert.AreEqual(0, (gpsStart.Value - start).TotalSeconds, 0.1 + 1e-9);
	}

	[TestMethod]
	public void Gap_IsTheTimeBetweenOneFilesEndAndTheNextStart()
	{
		var first = new DateTime(2026, 9, 14, 7, 33, 5);

		Assert.AreEqual(0, TelemetryExtraction.GapSeconds(first, 0, first.AddSeconds(564.65), 564.65)!.Value, 1e-6);
		Assert.AreEqual(26.4, TelemetryExtraction.GapSeconds(first, 0, first.AddSeconds(591.05), 564.65)!.Value, 1e-6);
		Assert.IsNull(TelemetryExtraction.GapSeconds(null, 0, first, 10));
	}
}
