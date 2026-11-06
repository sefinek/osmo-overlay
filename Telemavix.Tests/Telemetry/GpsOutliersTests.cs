using Telemavix.Core;
using Telemavix.Core.Cameras;
using Telemavix.Core.Ffmpeg;
using Telemavix.Core.Telemetry;

namespace Telemavix.Tests.Telemetry;

/// <summary>Implausible fixes and receiver speeds repaired before anything is derived from them (GpsOutliers).</summary>
[TestClass]
public sealed class GpsOutliersTests
{
	private const double Hz = 10;
	private const double MetersPerDegree = 111_320;

	/// <summary>`seconds` of fixes at Hz riding north at speedMs(t), the receiver reporting that speed.</summary>
	private static List<TelemetryFrame> Ride(double seconds, Func<double, double> speedMs)
	{
		List<TelemetryFrame> frames = [];
		double north = 0;
		for (int i = 0; i <= seconds * Hz; i++)
		{
			double t = i / Hz;
			if (i > 0) north += speedMs(t) / Hz;
			frames.Add(new TelemetryFrame(i, t, 50 + north / MetersPerDegree, 20, 200, null, 0, 1, 0, speedMs(t)));
		}

		return frames;
	}

	private static double NorthMeters(TelemetryFrame frame)
	{
		return (frame.Latitude - 50) * MetersPerDegree;
	}

	private static double EastMeters(TelemetryFrame frame)
	{
		return (frame.Longitude - 20) * MetersPerDegree * Math.Cos(50 * Math.PI / 180);
	}

	private static void Shift(List<TelemetryFrame> frames, double from, double to, double eastMeters)
	{
		for (int i = 0; i < frames.Count; i++)
		{
			if (frames[i].SampleTimeSeconds >= from && frames[i].SampleTimeSeconds < to)
				frames[i] = frames[i] with { Longitude = frames[i].Longitude + eastMeters / (MetersPerDegree * Math.Cos(50 * Math.PI / 180)) };
		}
	}

	[TestMethod]
	public void CleanRide_IsLeftAsItIs()
	{
		List<TelemetryFrame> frames = Ride(10, t => 10 + t);

		Assert.AreSame(frames, GpsOutliers.Repair(frames));
	}

	[TestMethod]
	public void FastRide_AndHardBraking_AreKept()
	{
		List<TelemetryFrame> fast = Ride(10, _ => 80);
		List<TelemetryFrame> braking = Ride(10, t => t < 5 ? 30 : Math.Max(0, 30 - (t - 5) * 9));

		Assert.AreSame(fast, GpsOutliers.Repair(fast), "288 km/h is fast, not impossible");
		Assert.AreSame(braking, GpsOutliers.Repair(braking), "0.9 g braking is real");
	}

	[TestMethod]
	public void MultipathJump_IsDrawnBackOntoTheTrack()
	{
		List<TelemetryFrame> frames = Ride(10, _ => 10);
		Shift(frames, 5, 5.5, 60);

		IReadOnlyList<TelemetryFrame> repaired = GpsOutliers.Repair(frames);

		Assert.IsTrue(repaired.All(f => Math.Abs(EastMeters(f)) < 0.01), "back on the road");
		TelemetryFrame middle = repaired.First(f => f.SampleTimeSeconds >= 5.2);
		Assert.AreEqual(NorthMeters(frames[(int)(5.2 * Hz)]), NorthMeters(middle), 0.5, "filled in by time between the fixes around it");
		Assert.IsTrue(repaired.All(f => f.HasGpsFix));
	}

	[TestMethod]
	public void Anomalies_AreTheRepairedStretches_WithWhatJumped()
	{
		List<TelemetryFrame> frames = Ride(10, _ => 10);
		Shift(frames, 5, 5.5, 60);
		frames[80] = frames[80] with { GpsSpeedMs = 30 };

		List<GpsAnomaly> anomalies = TelemetryProcessor.FindGpsAnomalies(frames);

		Assert.AreEqual(2, anomalies.Count);
		Assert.AreEqual((5.0, 5.4, true, false), (anomalies[0].Start, Math.Round(anomalies[0].End, 6), anomalies[0].Position, anomalies[0].Speed));
		Assert.AreEqual((8.0, 8.0, false, true), (anomalies[1].Start, anomalies[1].End, anomalies[1].Position, anomalies[1].Speed));
		Assert.AreEqual(0, TelemetryProcessor.FindGpsAnomalies(Ride(10, _ => 10)).Count);
	}

	[TestMethod]
	public void SpeedSpike_IsFilledInFromTheSamplesAround()
	{
		List<TelemetryFrame> frames = Ride(10, _ => 10);
		frames[50] = frames[50] with { GpsSpeedMs = 30 };

		Assert.AreEqual(10, GpsOutliers.Repair(frames)[50].GpsSpeedMs!.Value, 1e-9);
	}

	[TestMethod]
	public void RunsOfBadSpeeds_AreFilledInByTimeBetweenTheGoodOnes()
	{
		List<TelemetryFrame> frames = Ride(10, t => t < 5 ? 10 : 20);
		for (int i = 40; i <= 59; i++) frames[i] = frames[i] with { GpsSpeedMs = 500 };
		for (int i = 90; i < frames.Count; i++) frames[i] = frames[i] with { GpsSpeedMs = null };

		IReadOnlyList<TelemetryFrame> repaired = GpsOutliers.Repair(frames);

		Assert.AreEqual(10, repaired[39].GpsSpeedMs!.Value, 1e-9);
		for (int i = 40; i <= 59; i++)
		{
			double expected = 10 + (20 - 10) * (i - 39) / 21.0;
			Assert.AreEqual(expected, repaired[i].GpsSpeedMs!.Value, 0.6, $"frame {i}");
			Assert.IsTrue(repaired[i].GpsCorrected.HasFlag(GpsCorrection.Speed));
		}

		Assert.IsFalse(repaired[70].GpsCorrected.HasFlag(GpsCorrection.Speed));
		Assert.IsNull(repaired[95].GpsSpeedMs, "a missing reading is not a bad one");
	}

	[TestMethod]
	public void LongRunOfBadSpeeds_IsRepairedInLinearTime()
	{
		List<TelemetryFrame> frames = Ride(1500, _ => 10);
		for (int i = 1; i < frames.Count; i++) frames[i] = frames[i] with { GpsSpeedMs = 200 };

		var watch = System.Diagnostics.Stopwatch.StartNew();
		IReadOnlyList<TelemetryFrame> repaired = GpsOutliers.Repair(frames);
		watch.Stop();

		Assert.IsTrue(repaired.Skip(1).All(f => f.GpsSpeedMs == 10), "held at the last good reading");
		Assert.IsTrue(watch.ElapsedMilliseconds < 2000, $"{frames.Count} frames took {watch.ElapsedMilliseconds} ms");
	}

	[TestMethod]
	public void SpeedAboveAnyVehicle_IsAlwaysASpike()
	{
		List<TelemetryFrame> frames = Ride(10, _ => 10);
		for (int i = 40; i < frames.Count; i++) frames[i] = frames[i] with { GpsSpeedMs = 500 };

		IReadOnlyList<TelemetryFrame> repaired = GpsOutliers.Repair(frames);

		Assert.IsTrue(repaired.All(f => f.GpsSpeedMs == 10), "however long it lasts");
	}

	[TestMethod]
	public void ARealShift_LastingPastTheReset_IsTakenAsTheTrack()
	{
		List<TelemetryFrame> frames = Ride(10, _ => 10);
		Shift(frames, 3, 10.1, 80);

		IReadOnlyList<TelemetryFrame> repaired = GpsOutliers.Repair(frames);

		Assert.AreEqual(80, EastMeters(repaired[^1]), 0.01, "the new track is kept");
		Assert.AreEqual(80, EastMeters(repaired.First(f => f.SampleTimeSeconds >= 3)), 0.01, "from where it began, not only after the reset");
	}

	[TestMethod]
	public void StandingStill_FixWanderingIsKept()
	{
		List<TelemetryFrame> frames = Ride(10, _ => 0);
		var random = new Random(3);
		for (int i = 0; i < frames.Count; i++)
			frames[i] = frames[i] with { Latitude = frames[i].Latitude + (random.NextDouble() - 0.5) * 8 / MetersPerDegree };

		Assert.AreSame(frames, GpsOutliers.Repair(frames));
	}

	[TestMethod]
	public void AfterAGpsLoss_TheVehicleMayHaveGoneFar()
	{
		List<TelemetryFrame> frames = Ride(10, _ => 20);
		for (int i = 30; i < 70; i++)
			frames[i] = frames[i] with { HasGpsFix = false, Latitude = frames[29].Latitude, GpsSpeedMs = null };

		Assert.AreSame(frames, GpsOutliers.Repair(frames));
	}

	[TestMethod]
	public void AFileAfterAStop_StartsItsOwnTrack()
	{
		List<TelemetryFrame> frames = Ride(10, _ => 10);
		Shift(frames, 5, 10.1, 2000);
		frames[50] = frames[50] with { StartsAfterGap = true };

		Assert.AreSame(frames, GpsOutliers.Repair(frames));
	}

	[TestMethod]
	public void TestRecording_ComesOutAtItsRealSpeedAndDistance()
	{
		List<IReadOnlyList<TelemetryFrame>> files =
			[.. SyntheticRecording.Plans.Select((plan, i) => SyntheticTelemetry.ForFile(i, plan.Frames, SyntheticRecording.Fps))];
		var video = new VideoInfo("hevc", "Main 10", 3840, 2160, "60000/1001", "yuv420p10le", null, null, null, null, 90_000_000);
		List<SourceInfo> sources = [.. files.Select(f => new SourceInfo(video with { FrameCount = f.Count }, null, f.Count / SyntheticRecording.Fps, null))];
		IReadOnlyList<VideoSegment> segments = VideoSegments.Join([.. files.Select((_, i) => $"{i}.mp4")], sources);
		List<TelemetryFrame> frames = TelemetryExtraction.Combine(segments,
			segment => new TelemetryExtractionResult([.. files[int.Parse(Path.GetFileNameWithoutExtension(segment.InputPath))]], null)).Frames;

		List<DerivedFrame> derived = TelemetryProcessor.Process(frames, CameraFormats.Find("synthetic"));
		TelemetrySummary summary = TelemetryProcessor.Summarize(derived);

		Assert.IsTrue(summary.MaxSpeedKmh is > 145 and < 152, $"max speed {summary.MaxSpeedKmh:0.0} km/h - the multipath spike's 270 kept out");
		Assert.IsTrue(summary.TotalDistanceMeters is > 520 and < 640, $"distance {summary.TotalDistanceMeters:0} m - the jump there and back not counted");
		Assert.IsTrue(derived.Max(f => Math.Abs(f.RollDegrees)) < 45, "no lean past what the curve asks for");
	}
}
