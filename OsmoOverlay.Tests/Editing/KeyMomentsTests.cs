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
}
