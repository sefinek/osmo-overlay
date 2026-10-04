using OsmoOverlay.Core;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests;

[TestClass]
public sealed class KeyMomentsTests
{
	private const double Fps = 30;
	private const long Total = 300;

	private static List<DerivedFrame> Frames(Func<int, double> speedKmh, Func<int, double> altitude, Func<int, bool>? fix = null)
	{
		List<DerivedFrame> frames = [];
		for (int i = 0; i < 10; i++)
		{
			var raw = new TelemetryFrame(i * 30, i, 50, 20, altitude(i), null, 0, 0, 1, HasGpsFix: fix?.Invoke(i) ?? true);
			frames.Add(new DerivedFrame(raw, speedKmh(i), 0, 0, 0, 0, 0, default, 0, 0, 1, 0, 0));
		}

		return frames;
	}

	[TestMethod]
	public void MovingOnly_LeavesOutTheLeanOfStandingStill()
	{
		// The deepest lean at frame 2, standing (0 km/h), the next a second later, still starting off; riding, frame 7 leans the most.
		List<DerivedFrame> frames = [];
		for (int i = 0; i < 10; i++)
		{
			var raw = new TelemetryFrame(i * 30, i, 50, 20, 100, null, 0, 0, 1);
			frames.Add(new DerivedFrame(raw, i == 2 ? 0 : 20, 0, 0, 0, i == 2 ? 60 : i == 3 ? 40 : i == 7 ? 25 : 5, 0, default, 0, 0, 1, 0, 0));
		}

		Assert.AreEqual(60, KeyMoments.Find(frames, PeakKind.MaxLean, Fps, Total)!.Value.Value, 1e-9);
		Assert.AreEqual(25, KeyMoments.Find(frames, PeakKind.MaxLean, Fps, Total, TripStats.MovingThresholdKmh)!.Value.Value, 1e-9);
		Assert.AreEqual(20, KeyMoments.Find(frames, PeakKind.TopSpeed, Fps, Total, TripStats.MovingThresholdKmh)!.Value.Value, 1e-9, "speed unaffected");
		CollectionAssert.AreEqual(new[] { false, false, false, false, false, false, true, true, true, true }, TripStats.Riding(frames),
			"three seconds either side of a stop");
	}

	[TestMethod]
	public void Find_TopSpeed_IsTheFastestFrame()
	{
		Peak? peak = KeyMoments.Find(Frames(i => i == 6 ? 80 : 20, _ => 0), PeakKind.TopSpeed, Fps, Total);

		Assert.AreEqual(180, peak!.Value.Frame);
		Assert.AreEqual(80, peak.Value.Value);
	}

	[TestMethod]
	public void Find_LowestPoint_ReportsTheAltitudeNotItsNegative()
	{
		Peak? peak = KeyMoments.Find(Frames(_ => 10, i => i == 3 ? 12 : 100), PeakKind.LowestPoint, Fps, Total);

		Assert.AreEqual(90, peak!.Value.Frame);
		Assert.AreEqual(12, peak.Value.Value);
	}

	[TestMethod]
	public void Find_IgnoresFramesWithoutAGpsFix()
	{
		Peak? peak = KeyMoments.Find(Frames(i => i == 6 ? 200 : 20, _ => 0, i => i != 6), PeakKind.TopSpeed, Fps, Total);

		Assert.AreEqual(20, peak!.Value.Value);
	}

	[TestMethod]
	public void Find_ASlowRecordingHasNoTopSpeed()
	{
		Assert.IsNull(KeyMoments.Find(Frames(_ => 0.2, _ => 0), PeakKind.TopSpeed, Fps, Total));
	}

	[TestMethod]
	public void Find_WithoutFramesIsNull()
	{
		Assert.IsNull(KeyMoments.Find([], PeakKind.TopSpeed, Fps, Total));
	}

	[TestMethod]
	public void Add_KeepsTimeOrderAndReplacesTheSameFrame()
	{
		List<KeyMoment> moments = KeyMoments.Add([new KeyMoment(200, "b")], new KeyMoment(100, "a"), Total);
		moments = KeyMoments.Add(moments, new KeyMoment(200, "c"), Total);

		CollectionAssert.AreEqual(new[] { "a", "c" }, moments.Select(m => m.Name).ToArray());
	}

	[TestMethod]
	public void Add_IgnoresAFrameOutsideTheRecording()
	{
		Assert.AreEqual(0, KeyMoments.Add([], new KeyMoment(Total, "x"), Total).Count);
	}

	[TestMethod]
	public void Normalize_DropsWhatIsOutsideAndKeepsOnePerFrame()
	{
		List<KeyMoment> moments = KeyMoments.Normalize(
			[new KeyMoment(50, "late"), new KeyMoment(-1, "bad"), new KeyMoment(999, "bad"), new KeyMoment(50, "last"), new KeyMoment(10, "first")], Total);

		CollectionAssert.AreEqual(new[] { "first", "last" }, moments.Select(m => m.Name).ToArray());
	}

	[TestMethod]
	public void Markers_IncludeTheMoments()
	{
		SortedSet<long> markers = TimelineMarkers.Collect(Total, [], null, [], [new KeyMoment(120, "x")]);

		CollectionAssert.AreEqual(new long[] { 0, 120, 299 }, markers.ToArray());
	}

	// 2 m per sample (about 7 km/h at 1 sample a second), `altitude` per sample index.
	private static List<DerivedFrame> Walk(int count, Func<int, double> altitude)
	{
		List<DerivedFrame> frames = [];
		for (int i = 0; i < count; i++)
		{
			var raw = new TelemetryFrame(i * 30, i, 50, 20, altitude(i), null, 0, 0, 1);
			frames.Add(new DerivedFrame(raw, 7, 0, 0, i * 2.0, 0, 0, default, 0, 0, 1, 0, 0));
		}

		return frames;
	}

	[TestMethod]
	public void Find_SteepestClimb_IgnoresAltitudeNoiseOnTheFlat()
	{
		// +-1.5 m of GPS wobble from sample to sample: 2 m apart that reads as a 75% slope, over 100 m it's nothing.
		List<DerivedFrame> frames = Walk(300, i => 140 + (i % 2 == 0 ? 1.5 : -1.5));

		Assert.IsNull(KeyMoments.Find(frames, PeakKind.SteepestClimb, Fps, 9000));
		Assert.IsNull(KeyMoments.Find(frames, PeakKind.SteepestDescent, Fps, 9000));
	}

	[TestMethod]
	public void Find_SteepestClimb_FindsARealRampInTheMiddleOfIt()
	{
		// Flat, then 100 samples (200 m) rising 10 m (5%), then flat.
		List<DerivedFrame> frames = Walk(400, i => 100 + Math.Clamp(i - 150, 0, 100) * 0.1);

		Peak? peak = KeyMoments.Find(frames, PeakKind.SteepestClimb, Fps, 12000);

		Assert.AreEqual(5, peak!.Value.Value, 0.5);
		Assert.IsTrue(peak.Value.Frame is > 150 * 30 and < 250 * 30, $"{peak.Value.Frame}");
	}

	[TestMethod]
	public void Find_SteepestDescent_IsReportedNegative()
	{
		List<DerivedFrame> frames = Walk(400, i => 200 - Math.Clamp(i - 150, 0, 100) * 0.1);

		Peak? peak = KeyMoments.Find(frames, PeakKind.SteepestDescent, Fps, 12000);

		Assert.AreEqual(-5, peak!.Value.Value, 0.5);
	}

	[TestMethod]
	public void FindTop_ListsSeparatePeaksBestFirst()
	{
		double[] peaks = new double[300];
		(peaks[50], peaks[51], peaks[150], peaks[250]) = (40, 39, 30, 20);
		List<DerivedFrame> frames = Walk(300, _ => 0);
		for (int i = 0; i < frames.Count; i++) frames[i] = frames[i] with { SpeedKmh = 10 + peaks[i] };

		List<Peak> top = KeyMoments.FindTop(frames, PeakKind.TopSpeed, Fps, 9000, 3);

		CollectionAssert.AreEqual(new long[] { 50 * 30, 150 * 30, 250 * 30 }, top.Select(p => p.Frame).ToArray());
		CollectionAssert.AreEqual(new double[] { 50, 40, 30 }, top.Select(p => p.Value).ToArray());
	}
}
