using OsmoOverlay.Core;

namespace OsmoOverlay.Tests;

[TestClass]
public sealed class CutTransitionTests
{
	private static readonly CutTransition Fade = new(CutTransitionKind.FadeBlack, 1);
	private static readonly CutTransition White = new(CutTransitionKind.FadeWhite, 0.5);

	[TestMethod]
	public void Resolve_PutsTheCutsTransitionOnBothSidesOfTheJoin()
	{
		RenderPlan plan = RenderPlan.Resolve(null, null, [new TimeRange(10, 20, Fade), new TimeRange(40, 50, White)], null, 30, 30 * 100);

		Assert.AreEqual(3, plan.Pieces.Count);
		Assert.IsNull(plan.Pieces[0].TransitionIn);
		Assert.AreEqual(Fade, plan.Pieces[0].TransitionOut);
		Assert.AreEqual(Fade, plan.Pieces[1].TransitionIn);
		Assert.AreEqual(White, plan.Pieces[1].TransitionOut);
		Assert.AreEqual(White, plan.Pieces[2].TransitionIn);
		Assert.IsNull(plan.Pieces[2].TransitionOut);
	}

	[TestMethod]
	public void Resolve_IgnoresTheTransitionOfACutAtTheStartOrTheEnd()
	{
		RenderPlan plan = RenderPlan.Resolve(null, null, [new TimeRange(0, 5, Fade), new TimeRange(90, 100, Fade)], null, 30, 30 * 100);

		Assert.AreEqual(1, plan.Pieces.Count);
		Assert.IsNull(plan.Pieces[0].TransitionIn);
		Assert.IsNull(plan.Pieces[0].TransitionOut);
	}

	[TestMethod]
	public void Resolve_KeepsTheFirstTransitionOfOverlappingCuts_AndTheFrameCountsUnchanged()
	{
		RenderPlan with = RenderPlan.Resolve(null, null, [new TimeRange(10, 20, Fade), new TimeRange(15, 25, White)], null, 30, 30 * 100);
		RenderPlan without = RenderPlan.Resolve(null, null, [new TimeRange(10, 25)], null, 30, 30 * 100);

		Assert.AreEqual(Fade, with.Pieces[0].TransitionOut);
		Assert.AreEqual(without.TotalFrames, with.TotalFrames);
	}

	[TestMethod]
	public void CutList_KeepsTheTransitionThroughMergeAndResize()
	{
		List<FrameRange> cuts = CutList.Add([new FrameRange(100, 200, Fade)], new FrameRange(150, 300), 1000);
		Assert.AreEqual(Fade, cuts[0].Transition);

		cuts = CutList.Resize(cuts, 0, 120, 250, 1000);
		Assert.AreEqual(new FrameRange(120, 250, Fade), cuts[0]);
		Assert.AreEqual(Fade, cuts[0].ToTimeRange(30).Transition);
	}

	[TestMethod]
	public void HalfFrames_IsHalfTheLengthAndFitsTheirPiece()
	{
		Assert.AreEqual(30, new CutTransition(CutTransitionKind.FadeBlack, 2).HalfFrames(30, 1000));
		Assert.AreEqual(1, new CutTransition(CutTransitionKind.FadeBlack, 0.2).HalfFrames(5, 1000));
		Assert.AreEqual(2, new CutTransition(CutTransitionKind.FadeBlack, 5).HalfFrames(60, 4));
	}

	[TestMethod]
	public void TryParse_ReadsTheCliSuffix()
	{
		Assert.IsTrue(CutTransition.TryParse("fade=1.5", out CutTransition fade));
		Assert.AreEqual(new CutTransition(CutTransitionKind.FadeBlack, 1.5), fade);
		Assert.IsTrue(CutTransition.TryParse("white", out CutTransition white));
		Assert.AreEqual(CutTransitionKind.FadeWhite, white.Kind);
		Assert.AreEqual(CutTransition.DefaultLengthSeconds, white.LengthSeconds);
		Assert.IsFalse(CutTransition.TryParse("wipe", out _));
		Assert.IsFalse(CutTransition.TryParse("fade=abc", out _));
		Assert.AreEqual("fade=1.5", fade.ToArgument());
	}

	[TestMethod]
	public void FadeCurve_OutStartsUntouched_InStartsSolid_AndTheyMeetOnAFullFrame()
	{
		var piece = new RenderPiece(100, 60, Fade, Fade);
		int n = Fade.HalfFrames(30, 60);

		Assert.AreEqual(0.0, CutTransitionFade.At(piece, 30, 30).Amount);
		Assert.AreEqual(1.0, CutTransitionFade.At(piece, 0, 30).Amount);
		Assert.IsTrue(CutTransitionFade.At(piece, 59, 30).Amount > 0.9 && CutTransitionFade.At(piece, 59, 30).Amount < 1);
		Assert.AreEqual(0.0, CutTransitionFade.At(piece, 60 - n, 30).Amount);
	}

	[TestMethod]
	public void Apply_BlendsTowardTheColorAndKeepsAlpha()
	{
		byte[] black = [200, 100, 50, 255];
		CutTransitionFade.Apply(black, 0.5, false);
		CollectionAssert.AreEqual(new byte[] { 100, 50, 25, 255 }, black);

		byte[] white = [0, 0, 0, 255];
		CutTransitionFade.Apply(white, 1, true);
		CollectionAssert.AreEqual(new byte[] { 255, 255, 255, 255 }, white);

		byte[] untouched = [10, 20, 30, 255];
		CutTransitionFade.Apply(untouched, 0, true);
		CollectionAssert.AreEqual(new byte[] { 10, 20, 30, 255 }, untouched);
	}

	private static readonly CutTransition Cross = new(CutTransitionKind.Crossfade, 1);

	[TestMethod]
	public void Resolve_OverlapShortensTheOutputByTheTransitionLength()
	{
		RenderPlan plan = RenderPlan.Resolve(null, null, [new TimeRange(10, 20, Cross)], null, 30, 30 * 100);

		Assert.AreEqual(30, plan.Pieces[1].OverlapIn);
		Assert.AreEqual(0, plan.Pieces[0].OverlapIn);
		Assert.AreEqual(plan.Pieces.Sum(p => p.FrameCount) - 30, plan.TotalFrames);
	}

	[TestMethod]
	public void Resolve_OverlapIsAtMostHalfOfTheShorterPart_SoNeighbouringTransitionsNeverMeet()
	{
		RenderPlan plan = RenderPlan.Resolve(null, null, [new TimeRange(2, 3, Cross), new TimeRange(4, 5, Cross)], null, 30, 30 * 10);

		Assert.AreEqual(15, plan.Pieces[1].OverlapIn);
		Assert.AreEqual(15, plan.Pieces[2].OverlapIn);
		Assert.IsTrue(plan.Pieces[1].OverlapIn + plan.Pieces[2].OverlapIn <= plan.Pieces[1].FrameCount);
	}

	[TestMethod]
	public void Resolve_APartTooShortToOverlapIsAPlainCut()
	{
		RenderPlan plan = RenderPlan.Resolve(null, null, [new TimeRange(1.0, 5, Cross)], null, 30, 30 * 10);
		Assert.IsTrue(plan.Pieces[1].OverlapIn > 0);

		RenderPlan tiny = RenderPlan.Resolve(null, null, [new TimeRange(0.03, 5, Cross)], null, 30, 30 * 10);
		Assert.AreEqual(2, tiny.Pieces.Count);
		Assert.AreEqual(0, tiny.Pieces[1].OverlapIn);
		Assert.AreEqual(tiny.Pieces.Sum(p => p.FrameCount), tiny.TotalFrames);
	}

	[TestMethod]
	public void OutputTimeline_StartsAnOverlappingPieceWhileTheEarlierOneIsStillOnScreen()
	{
		RenderPlan plan = RenderPlan.Resolve(null, null, [new TimeRange(10, 20, Cross)], null, 30, 30 * 100);
		var timeline = new OutputTimeline(plan, 30);

		Assert.AreEqual(plan.TotalFrames / 30.0, timeline.DurationSeconds, 1e-9);
		// The later piece's first frame lands 1 s before the earlier piece's end.
		Assert.AreEqual(9.0, timeline.ToOutputSeconds(20.0)!.Value, 1e-9);
		// Playback skips those frames - the earlier piece already showed them.
		Assert.AreEqual(21.0, timeline.NextKeptStretch(10.0)!.Value.Start, 1e-9);
	}

	[TestMethod]
	public void Blend_WipeAndSlideMoveFromTheSideFfmpegDoes()
	{
		byte[] Solid(byte v) => [.. Enumerable.Repeat(v, 8 * 1 * 4)];
		byte[] scratch = new byte[8 * 4];

		byte[] a = Solid(10);
		CutTransitionBlend.Blend(a, Solid(200), scratch, 8, 1, CutTransitionKind.WipeLeft, 0.5);
		Assert.AreEqual(10, a[0]);
		Assert.AreEqual(200, a[7 * 4]);

		a = Solid(10);
		CutTransitionBlend.Blend(a, Solid(200), scratch, 8, 1, CutTransitionKind.WipeRight, 0.5);
		Assert.AreEqual(200, a[0]);
		Assert.AreEqual(10, a[7 * 4]);

		a = Solid(10);
		CutTransitionBlend.Blend(a, Solid(200), scratch, 8, 1, CutTransitionKind.SlideRight, 0.25);
		Assert.AreEqual(200, a[0]);
		Assert.AreEqual(10, a[7 * 4]);

		a = Solid(10);
		CutTransitionBlend.Blend(a, Solid(200), scratch, 8, 1, CutTransitionKind.Crossfade, 0.5);
		Assert.AreEqual(105, a[0]);
	}

	[TestMethod]
	public void Resolve_FrameLimitLeavesNoTransitionAtTheArtificialEnd()
	{
		RenderPlan plan = RenderPlan.Resolve(null, null, [new TimeRange(10, 20, Fade)], 400, 30, 30 * 100);

		Assert.AreEqual(2, plan.Pieces.Count);
		Assert.IsNull(plan.Pieces[1].TransitionOut);
	}
}
