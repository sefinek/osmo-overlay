using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Rendering;

/// <summary>A cut's caption: from the plan to the output's timeline to the picture.</summary>
[TestClass]
public sealed class CutCaptionTests
{
	private const double Fps = 30;

	private static RenderPlan PlanWithCaptions(CutCaption? first, CutCaption? second = null)
	{
		// 20 s: 5-8 s and 12-15 s cut out.
		List<TimeRange> cuts = [new(5, 8, null, first), new(12, 15, null, second)];
		return RenderPlan.Resolve(null, null, cuts, null, Fps, (long)(20 * Fps));
	}

	[TestMethod]
	public void Plan_GivesTheCaptionToThePieceAfterItsCut()
	{
		RenderPlan plan = PlanWithCaptions(new CutCaption("a unit", 2));

		Assert.AreEqual(3, plan.Pieces.Count);
		Assert.IsNull(plan.Pieces[0].CaptionIn);
		Assert.AreEqual("a unit", plan.Pieces[1].CaptionIn!.Text);
		Assert.IsNull(plan.Pieces[2].CaptionIn);
	}

	[TestMethod]
	public void Plan_NoCaptionAtACutWithNothingToJoin()
	{
		var cuts = new List<TimeRange> { new(0, 3, null, new CutCaption("at the start")), new(17, 20, null, new CutCaption("at the end")) };
		var plan = RenderPlan.Resolve(null, null, cuts, null, Fps, (long)(20 * Fps));

		Assert.AreEqual(1, plan.Pieces.Count);
		Assert.IsNull(plan.Pieces[0].CaptionIn);
		Assert.AreEqual(0, new OutputTimeline(plan, Fps).Captions.Count);
	}

	[TestMethod]
	public void OutputTimeline_PlacesCaptionsWhereTheLaterPartStarts()
	{
		var timeline = new OutputTimeline(PlanWithCaptions(new CutCaption("first", 2), new CutCaption("second")), Fps);

		// Kept: 0-5, 8-12, 15-20 - the joins are at 5 s and 9 s of the output.
		IReadOnlyList<CutCaptionAt> captions = timeline.Captions;
		Assert.AreEqual(2, captions.Count);
		Assert.AreEqual(5.0, captions[0].StartSeconds, 1e-9);
		Assert.AreEqual(7.0, captions[0].EndSeconds, 1e-9);
		Assert.AreEqual(9.0, captions[1].StartSeconds, 1e-9);
		Assert.AreEqual(12.0, captions[1].EndSeconds, 1e-9);
	}

	[TestMethod]
	public void OutputTimeline_CaptionStartsWithTheTransition()
	{
		// Kept 0-5 and 8-20: the join is at 5 s of the output.
		RenderPlan Plan(CutTransition transition)
		{
			List<TimeRange> cuts = [new(5, 8, transition, new CutCaption("x", 2))];
			return RenderPlan.Resolve(null, null, cuts, null, Fps, (long)(20 * Fps));
		}

		var dip = new OutputTimeline(Plan(new CutTransition(CutTransitionKind.FadeBlack)), Fps);
		Assert.AreEqual(4.5, dip.Captions[0].StartSeconds, 1e-9, "a dip starts fading the earlier part out half its length before the join");

		var cross = new OutputTimeline(Plan(new CutTransition(CutTransitionKind.Crossfade)), Fps);
		Assert.AreEqual(4.0, cross.Captions[0].StartSeconds, 1e-9, "an overlap starts where the later part comes in, a full length before the join");
		Assert.AreEqual(6.0, cross.Captions[0].EndSeconds, 1e-9);

		Assert.AreEqual(1.0, dip.Captions[0].FadeSeconds, 1e-9, "the caption fades as long as the transition does");
		Assert.AreEqual(1.0, cross.Captions[0].FadeSeconds, 1e-9);
		Assert.AreEqual(0.8, new OutputTimeline(Plan(new CutTransition(CutTransitionKind.Crossfade, 0.8)), Fps).Captions[0].FadeSeconds, 1e-9);

		var plain = new OutputTimeline(RenderPlan.Resolve(null, null, [new TimeRange(5, 8, null, new CutCaption("x", 2))], null, Fps, (long)(20 * Fps)), Fps);
		Assert.AreEqual(CutCaptionAt.DefaultFadeSeconds, plain.Captions[0].FadeSeconds, 1e-9, "no transition: the default fade");
	}

	[TestMethod]
	public void OutputTimeline_CaptionNeverRunsPastTheEnd()
	{
		var cuts = new List<TimeRange> { new(5, 8, null, new CutCaption("late", 30)) };
		var timeline = new OutputTimeline(RenderPlan.Resolve(null, null, cuts, null, Fps, (long)(20 * Fps)), Fps);

		Assert.AreEqual(timeline.DurationSeconds, timeline.Captions[0].EndSeconds, 1e-9);
	}

	[TestMethod]
	public void CutList_AMergedCutKeepsTheCaptionOfTheOneReachingTheJoin()
	{
		List<FrameRange> merged = CutList.Normalize(
		[
			new FrameRange(100, 200, null, new CutCaption("early")),
			new FrameRange(150, 300),
			new FrameRange(250, 400, null, new CutCaption("later"))
		], 1000);

		Assert.AreEqual(1, merged.Count);
		Assert.AreEqual("later", merged[0].Caption!.Text);
		Assert.AreEqual("early", CutList.Normalize([new FrameRange(100, 200, null, new CutCaption("early")), new FrameRange(150, 300)], 1000)[0].Caption!.Text,
			"an unset one keeps the other's");
	}

	[TestMethod]
	public void CutCaption_BlankIsNoCaption_AndTheLengthIsClamped()
	{
		Assert.IsNull(CutCaption.Of("  ", 3));
		Assert.IsNull(CutCaption.Of(null, 3));
		Assert.AreEqual("hello", CutCaption.Of("  hello ", 3)!.Text);
		Assert.AreEqual(CutCaption.MaxSeconds, CutCaption.Of("x", 999)!.Seconds);
		Assert.AreEqual(CutCaption.MinSeconds, CutCaption.Of("x", 0)!.Seconds);
	}

	[TestMethod]
	public void Project_KeepsCaptionsInItsFile()
	{
		string path = Path.Combine(Path.GetTempPath(), $"caption-{Guid.NewGuid():N}.ovproj");
		try
		{
			var project = new OverlayProject([Path.Combine(Path.GetTempPath(), "clip.mp4")], null,
				[new FrameRange(10, 20, new CutTransition(CutTransitionKind.Crossfade), new CutCaption("a unit\nwas here", 4))], null, 0, null, null);
			project.Save(path);

			CutCaption? back = OverlayProject.Load(path).Cuts[0].Caption;
			Assert.AreEqual("a unit\nwas here", back!.Text);
			Assert.AreEqual(4.0, back.Seconds);
		}
		finally
		{
			File.Delete(path);
		}
	}

	private static List<DerivedFrame> Frames(double seconds)
	{
		return
		[
			.. Enumerable.Range(0, (int)(seconds * Fps)).Select(i =>
			{
				double t = i / Fps;
				return new DerivedFrame(new TelemetryFrame(i, t, 50, 20, 200, null, 0, 1, 0), 20, 0, 0, t, 0, 0, new SunPosition(120, 30), 0, 0, 1, 0, 0);
			})
		];
	}

	private static int DrawnPixels(OverlayRenderer renderer, List<DerivedFrame> frames, double seconds)
	{
		byte[] pixels = new byte[renderer.FrameBufferSize()];
		renderer.RenderInto(TelemetryProcessor.FindNearest(frames, seconds), pixels, premultiplied: true);
		int drawn = 0;
		for (int i = 3; i < pixels.Length; i += 4)
		{
			if (pixels[i] > 0) drawn++;
		}

		return drawn;
	}

	[TestMethod]
	public void Renderer_DrawsTheCaptionOnlyWhileItIsUp_FadingInAndOut()
	{
		List<DerivedFrame> frames = Frames(10);
		using var renderer = new OverlayRenderer(1920, 1080, 200, [], frames, 20, false)
		{
			CutCaptions = [new CutCaptionAt(4, 7, "A military unit was cut out here")]
		};

		Assert.AreEqual(0, DrawnPixels(renderer, frames, 3.5), "before");
		Assert.AreEqual(0, DrawnPixels(renderer, frames, 8), "after");
		int full = DrawnPixels(renderer, frames, 5.5);
		Assert.IsTrue(full > 20_000, "a panel at the middle of the frame");
		Assert.IsTrue(DrawnPixels(renderer, frames, 4.1) is > 0 && DrawnPixels(renderer, frames, 4.1) <= full, "the way in");
		Assert.AreEqual(full, DrawnPixels(renderer, frames, 5.5), "the same frame draws the same again");
	}

	[TestMethod]
	public void Renderer_WrapsALongTextToMoreLines()
	{
		List<DerivedFrame> frames = Frames(10);
		string words = string.Join(' ', Enumerable.Repeat("military", 14));
		using var single = new OverlayRenderer(1920, 1080, 200, [], frames, 20, false) { CutCaptions = [new CutCaptionAt(0, 9, "military")] };
		using var wrapped = new OverlayRenderer(1920, 1080, 200, [], frames, 20, false) { CutCaptions = [new CutCaptionAt(0, 9, words)] };

		Assert.IsTrue(DrawnPixels(wrapped, frames, 4) > DrawnPixels(single, frames, 4) * 2, "a long text makes a bigger panel, wrapped at its maximum width");
	}
}
