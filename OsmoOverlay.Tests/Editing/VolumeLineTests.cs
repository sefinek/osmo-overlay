using OsmoOverlay.Core;

namespace OsmoOverlay.Tests;

[TestClass]
public sealed class VolumeLineTests
{
	private const double Duration = 100;
	private const double Fps = 25;

	private static VolumePoint P(double seconds, double db)
	{
		return new VolumePoint(seconds, db);
	}

	[TestMethod]
	public void Normalize_SortsClampsAndKeepsOnePointPerMoment()
	{
		List<VolumePoint> line = VolumeLine.Normalize([P(50, -10), P(-5, -20), P(50.001, -30), P(200, 99)], Duration);

		CollectionAssert.AreEqual(new[] { P(0, -20), P(50.001, -30), P(100, VolumeLine.MaxDb) }, line);
	}

	[TestMethod]
	public void Normalize_AllPointsAtZero_IsTheRecordingsOwnSound()
	{
		Assert.AreEqual(0, VolumeLine.Normalize([P(0, 0), P(10, 0.01)], Duration).Count);
	}

	[TestMethod]
	public void DbAt_HoldsTheEndsAndRunsStraightBetweenPoints()
	{
		List<VolumePoint> line = [P(10, -10), P(20, -30)];

		Assert.AreEqual(-10, VolumeLine.DbAt(line, 0));
		Assert.AreEqual(-20, VolumeLine.DbAt(line, 15), 1e-9);
		Assert.AreEqual(-30, VolumeLine.DbAt(line, 90));
		Assert.AreEqual(0, VolumeLine.DbAt([], 5));
	}

	[TestMethod]
	public void Gain_IsOffAtTheBottomOfTheRange()
	{
		Assert.AreEqual(0, VolumeLine.Gain(VolumeLine.SilentDb));
		Assert.AreEqual(1, VolumeLine.Gain(0), 1e-12);
		Assert.AreEqual(0.5, VolumeLine.Gain(-6.0206), 1e-4);
	}

	[TestMethod]
	public void Add_PutsAPointOnTheLineWithoutChangingIt()
	{
		List<VolumePoint> line = [P(10, -10), P(20, -30)];

		List<VolumePoint> added = VolumeLine.Add(line, 15, Duration);

		Assert.AreEqual(3, added.Count);
		Assert.AreEqual(-20, added[1].Db, 1e-9);
	}

	[TestMethod]
	public void Move_StaysBetweenItsNeighbours()
	{
		List<VolumePoint> line = [P(10, -10), P(20, -10), P(30, -10)];

		List<VolumePoint> moved = VolumeLine.Move(line, 1, 99, -20, Duration);

		Assert.AreEqual(30 - VolumeLine.MinGapSeconds, moved[1].Seconds, 1e-9);
		Assert.AreEqual(-20, moved[1].Db);
	}

	[TestMethod]
	public void Shift_MovesTheTwoPointsEitherSideOfTheTime_AndKeepsTheirSlope()
	{
		List<VolumePoint> line = [P(10, 0), P(20, -10), P(30, -20)];

		List<VolumePoint> shifted = VolumeLine.Shift(line, 25, -5, Duration);

		CollectionAssert.AreEqual(new[] { P(10, 0), P(20, -15), P(30, -25) }, shifted);
	}

	[TestMethod]
	public void Shift_BeforeTheFirstPoint_MovesThatPointOnly()
	{
		List<VolumePoint> shifted = VolumeLine.Shift([P(10, -5), P(20, -5)], 2, -10, Duration);

		CollectionAssert.AreEqual(new[] { P(10, -15), P(20, -5) }, shifted);
	}

	[TestMethod]
	public void Shift_IsCutDownSoNoPointLeavesTheRange()
	{
		List<VolumePoint> shifted = VolumeLine.Shift([P(10, 0), P(20, -40)], 15, 30, Duration);

		Assert.IsTrue(shifted.All(p => p.Db <= VolumeLine.MaxDb));
		Assert.AreEqual(40, shifted[0].Db - shifted[1].Db, 1e-9, "the slope is kept");
	}

	[TestMethod]
	public void Shift_WithoutPoints_SetsOneLevelForTheWholeRecording()
	{
		List<VolumePoint> shifted = VolumeLine.Shift([], 30, -12, Duration);

		CollectionAssert.AreEqual(new[] { P(0, -12), P(Duration, -12) }, shifted);
	}

	[TestMethod]
	public void FadeIn_StartsFromSilenceAndEndsAtTheLinesLevel_AndCanBeTakenAway()
	{
		List<VolumePoint> faded = VolumeLine.WithFadeIn([P(0, -6), P(Duration, -6)], 2, Duration);

		CollectionAssert.AreEqual(new[] { P(0, VolumeLine.SilentDb), P(2, -6), P(Duration, -6) }, faded);
		Assert.AreEqual(2, VolumeLine.FadeInSeconds(faded));
		Assert.AreEqual(0, VolumeLine.FadeInSeconds(VolumeLine.WithFadeIn(faded, 0, Duration)));
	}

	[TestMethod]
	public void FadeOut_WithoutOtherPoints_RunsFromTheRecordingsOwnLevelToSilence()
	{
		List<VolumePoint> faded = VolumeLine.WithFadeOut([], 3, Duration);

		CollectionAssert.AreEqual(new[] { P(97, 0), P(Duration, VolumeLine.SilentDb) }, faded);
		Assert.AreEqual(3, VolumeLine.FadeOutSeconds(faded, Duration), 1e-9);
		Assert.AreEqual(0, VolumeLine.WithFadeOut(faded, 0, Duration).Count);
	}

	[TestMethod]
	public void ForOutput_WithoutCuts_IsTheLineItself()
	{
		var plan = new RenderPlan([new RenderPiece(0, (long)(Duration * Fps))], false);

		List<VolumeSegment> segments = VolumeLine.ForOutput([P(10, 0), P(20, -10)], plan, Fps);

		CollectionAssert.AreEqual(new[]
		{
			new VolumeSegment(0, 10, 0, 0),
			new VolumeSegment(10, 20, 0, -10),
			new VolumeSegment(20, Duration, -10, -10)
		}, segments);
	}

	[TestMethod]
	public void ForOutput_ACutOutPartIsLeftOut_AndTheLevelAtItsEdgesIsKept()
	{
		// Frames 250-500 (seconds 10-20) are cut out of a line running from 0 dB at 0 s to -20 dB at 40 s.
		var plan = new RenderPlan([new RenderPiece(0, 250), new RenderPiece(500, 500)], true);

		List<VolumeSegment> segments = VolumeLine.ForOutput([P(0, 0), P(40, -20)], plan, Fps);

		Assert.AreEqual(0, segments[0].T0);
		Assert.AreEqual(10, segments[0].T1, 1e-9);
		Assert.AreEqual(-5, segments[0].Db1, 1e-9);
		Assert.AreEqual(10, segments[1].T0, 1e-9);
		Assert.AreEqual(-10, segments[1].Db0, 1e-9, "the output goes on at the level of the recording's second 20");
		Assert.AreEqual(30, segments[^1].T1, 1e-9);
	}

	[TestMethod]
	public void FfmpegExpression_NestsASegmentEach_AndWritesNumbersWithADot()
	{
		string expression = VolumeLine.FfmpegExpression(
		[
			new VolumeSegment(0, 1.5, 0, 0),
			new VolumeSegment(1.5, 3, 0, VolumeLine.SilentDb),
			new VolumeSegment(3, 5, VolumeLine.SilentDb, VolumeLine.SilentDb)
		]);

		Assert.AreEqual("if(lt(t,1.5),1,if(lt(t,3),pow(10,(0+(-48)*(t-1.5)/1.5)/20),0))", expression);
	}

	[TestMethod]
	public void FfmpegExpression_ConstantLevel_IsItsGain()
	{
		Assert.AreEqual("0.501187", VolumeLine.FfmpegExpression([new VolumeSegment(0, 5, -6, -6)]));
	}
}
