using OsmoOverlay.Core;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests;

[TestClass]
public sealed class OutputTimelineTests
{
	private const double Fps = 10;
	private const double MetersPerDegreeLat = 6_371_000 * Math.PI / 180;

	/// <summary>100 s northwards at 10 m/s, one telemetry sample per video frame at 10 fps.</summary>
	private static List<DerivedFrame> Recording()
	{
		List<TelemetryFrame> raw =
		[
			.. Enumerable.Range(0, 1000).Select(i => new TelemetryFrame(i, i / Fps, 50 + 10 * (i / Fps) / MetersPerDegreeLat, 20, 200,
				new DateTime(2026, 9, 23, 12, 0, 0).AddSeconds(i / Fps), 0, 0, 1, 10))
		];
		return TelemetryProcessor.Process(raw);
	}

	private static OutputTimeline Timeline(params TimeRange[] cuts)
	{
		return new OutputTimeline(RenderPlan.Resolve(10, 60, cuts, null, Fps, 1000), Fps);
	}

	[TestMethod]
	public void OutputTime_StartsAtZero_AndSkipsCutParts()
	{
		OutputTimeline timeline = Timeline(new TimeRange(20, 30));

		Assert.IsNull(timeline.ToOutputSeconds(5), "before the range");
		Assert.AreEqual(0, timeline.ToOutputSeconds(10)!.Value, 1e-9);
		Assert.AreEqual(9.9, timeline.ToOutputSeconds(19.9)!.Value, 1e-9);
		Assert.IsNull(timeline.ToOutputSeconds(25), "inside the cut");
		Assert.AreEqual(10, timeline.ToOutputSeconds(30)!.Value, 1e-9, "the next piece continues right where the first ended");
		Assert.IsNull(timeline.ToOutputSeconds(60), "the end is exclusive");
	}

	[TestMethod]
	public void MapFrames_KeepsOnlyKeptFrames_OnTheOutputTimeline()
	{
		List<DerivedFrame> mapped = Timeline(new TimeRange(20, 30)).MapFrames(Recording());

		Assert.AreEqual(400, mapped.Count);
		CollectionAssert.AreEqual(Enumerable.Range(0, 400).Select(i => Math.Round(i / Fps, 6)).ToArray(),
			mapped.Select(f => Math.Round(f.Raw.SampleTimeSeconds, 6)).ToArray());
		Assert.AreEqual(30, mapped[100].Raw.RecordingTimeSeconds, 1e-9, "the recording time is kept alongside");
		Assert.AreEqual(new DateTime(2026, 9, 23, 12, 0, 30), mapped[100].Raw.GpsTimestamp, "the wall clock stays real");
	}

	[TestMethod]
	public void MapFrames_DistanceCountsOnlyWhatIsShown()
	{
		List<DerivedFrame> mapped = Timeline(new TimeRange(20, 30)).MapFrames(Recording());

		Assert.AreEqual(0, mapped[0].CumulativeDistanceMeters, "starts at zero at the first kept frame, not at the recording's start");
		// 10 s + 30 s kept at 10 m/s - the 10 s cut and the jump across it add nothing.
		Assert.AreEqual(399, mapped[^1].CumulativeDistanceMeters, 1);
		Assert.AreEqual(399, TelemetryProcessor.Summarize(mapped).TotalDistanceMeters, 1);
		Assert.AreEqual(39.9, TelemetryProcessor.Summarize(mapped).DurationSeconds, 1e-6);
	}

	[TestMethod]
	public void MapFrames_KeepsSpeedFromTheWholeRecording()
	{
		List<DerivedFrame> mapped = Timeline(new TimeRange(20, 30)).MapFrames(Recording());

		Assert.IsTrue(mapped.All(f => Math.Abs(f.SpeedKmh - 36) < 1e-6), "no speed spike at the join");
	}

	[TestMethod]
	public void NextKeptStretch_InsideAPiece_ContinuesFromThere()
	{
		Assert.AreEqual((15.0, 20.0), Timeline(new TimeRange(20, 30)).NextKeptStretch(15));
	}

	[TestMethod]
	public void NextKeptStretch_InACut_JumpsToTheNextPiece()
	{
		OutputTimeline timeline = Timeline(new TimeRange(20, 30));

		Assert.AreEqual((10.0, 20.0), timeline.NextKeptStretch(0), "before the range");
		Assert.AreEqual((30.0, 60.0), timeline.NextKeptStretch(25), "inside the cut");
	}

	[TestMethod]
	public void NextKeptStretch_FromAPieceEnd_GivesThePieceAfterIt()
	{
		Assert.AreEqual((30.0, 60.0), Timeline(new TimeRange(20, 30)).NextKeptStretch(20));
	}

	[TestMethod]
	public void NextKeptStretch_PastTheLastPiece_IsNull()
	{
		OutputTimeline timeline = Timeline(new TimeRange(20, 30));

		Assert.IsNull(timeline.NextKeptStretch(60));
		Assert.IsNull(timeline.NextKeptStretch(95));
	}
}
