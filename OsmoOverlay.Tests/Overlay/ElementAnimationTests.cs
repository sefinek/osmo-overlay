using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class ElementAnimationTests
{
	private static TextElement Timed(OverlayAnimationType inType, double inSeconds, OverlayAnimationType? outType = null, double? outSeconds = null)
	{
		return new TextElement
		{
			X = 0, Y = 0, AppearAtSeconds = 10, DisappearAtSeconds = 20,
			AnimationType = inType, AnimationDurationSeconds = inSeconds,
			OutAnimationType = outType, OutAnimationDurationSeconds = outSeconds
		};
	}

	[TestMethod]
	public void NoTiming_IsAlwaysShown()
	{
		Assert.AreEqual(ElementState.Shown, ElementAnimation.At(new TextElement { X = 0, Y = 0 }, 123));
	}

	[TestMethod]
	public void NeverDisappearing_LeavesWithTheVideosEnd()
	{
		TextElement element = Timed(OverlayAnimationType.Fade, 1, OverlayAnimationType.SlideRight, 4) with { DisappearAtSeconds = null };

		Assert.AreEqual(ElementState.Shown, ElementAnimation.At(element, 50, 60));
		Assert.AreEqual(new ElementState(0.5f, OverlayAnimationType.SlideRight, true), ElementAnimation.At(element, 58, 60));
		Assert.AreEqual(ElementState.Shown, ElementAnimation.At(element, 58), "without the video's length there's no end to leave at");
	}

	[TestMethod]
	public void AWayOutAlone_WithNoTimesSet_StillPlaysAtTheEnd()
	{
		var element = new TextElement { X = 0, Y = 0, OutAnimationType = OverlayAnimationType.Fade, OutAnimationDurationSeconds = 2 };

		Assert.AreEqual(0.5f, ElementAnimation.At(element, 59, 60).Progress);
	}

	[TestMethod]
	public void None_IsAHardCutAtBothEnds()
	{
		TextElement element = Timed(OverlayAnimationType.None, 1);

		Assert.AreEqual(0f, ElementAnimation.At(element, 9.99).Progress);
		Assert.AreEqual(1f, ElementAnimation.At(element, 10).Progress);
		Assert.AreEqual(1f, ElementAnimation.At(element, 19.99).Progress);
		Assert.AreEqual(0f, ElementAnimation.At(element, 20).Progress);
	}

	[TestMethod]
	public void WithoutAWayOut_ItLeavesWithAHardCut()
	{
		TextElement element = Timed(OverlayAnimationType.SlideUp, 2);

		Assert.AreEqual(new ElementState(0.5f, OverlayAnimationType.SlideUp, false), ElementAnimation.At(element, 11));
		Assert.AreEqual(ElementState.Shown, ElementAnimation.At(element, 19.99));
		Assert.AreEqual(ElementState.Hidden, ElementAnimation.At(element, 20));
	}

	[TestMethod]
	public void AWayInAlone_DoesntLeaveAtTheVideosEnd()
	{
		TextElement element = Timed(OverlayAnimationType.Fade, 1) with { DisappearAtSeconds = null };

		Assert.AreEqual(ElementState.Shown, ElementAnimation.At(element, 59.9, 60));
	}

	[TestMethod]
	public void WayInAndWayOut_AreSeparate()
	{
		TextElement element = Timed(OverlayAnimationType.Fade, 1, OverlayAnimationType.SlideLeft, 4);

		Assert.AreEqual(new ElementState(0.5f, OverlayAnimationType.Fade, false), ElementAnimation.At(element, 10.5));
		Assert.AreEqual(ElementState.Shown, ElementAnimation.At(element, 15.9));
		Assert.AreEqual(new ElementState(0.25f, OverlayAnimationType.SlideLeft, true), ElementAnimation.At(element, 19));
	}

	[TestMethod]
	public void HardCutIn_CanStillAnimateOut()
	{
		TextElement element = Timed(OverlayAnimationType.None, 1, OverlayAnimationType.Fade, 2);

		Assert.AreEqual(1f, ElementAnimation.At(element, 10).Progress);
		Assert.AreEqual(new ElementState(0.5f, OverlayAnimationType.Fade, true), ElementAnimation.At(element, 19));
	}

	[TestMethod]
	public void ShortWindow_TheWayOutWaitsForTheWayIn()
	{
		// 10 s on screen, 8 s in and 8 s out: out starts only at 18 s, when the way in ends.
		TextElement element = Timed(OverlayAnimationType.Fade, 8, OverlayAnimationType.Fade, 8);

		Assert.IsFalse(ElementAnimation.At(element, 17).Leaving);
		Assert.AreEqual(new ElementState(0.5f, OverlayAnimationType.Fade, true), ElementAnimation.At(element, 19));
	}
}
