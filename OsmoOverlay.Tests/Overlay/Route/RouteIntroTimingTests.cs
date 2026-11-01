using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Overlay;

/// <summary>Widgets' timing next to the route intro card: what the timeline shows (OverlayRenderer.IntroShiftSeconds, RouteIntroSpan).</summary>
[TestClass]
public sealed class RouteIntroTimingTests
{
	private static OverlayRenderer Renderer(bool intro)
	{
		List<DerivedFrame> frames =
		[
			.. Enumerable.Range(0, 60).Select(i => new DerivedFrame(new TelemetryFrame(i, i / 30.0, 50, 20, 200, null, 0, 0, 1), 20, 0, 2, i, 0, 0,
				new SunPosition(120, 30), 0, 0, 1, 0, 0))
		];
		RouteIntroSettings routeIntro = intro ? RouteIntroSettings.Disabled with { Enabled = true, DurationSeconds = 5 } : RouteIntroSettings.Disabled;
		return new OverlayRenderer(640, 360, 200, [], frames, 20, routeIntro: routeIntro);
	}

	[TestMethod]
	public void Watermark_CountsFromTheCardsEnd_OnlyWithTheCard()
	{
		using OverlayRenderer withCard = Renderer(true);
		using OverlayRenderer without = Renderer(false);

		Assert.AreEqual(5, withCard.IntroShiftSeconds(new WatermarkElement { X = 0, Y = 0 }));
		Assert.AreEqual(0, without.IntroShiftSeconds(new WatermarkElement { X = 0, Y = 0 }));
		Assert.AreEqual(0, withCard.IntroShiftSeconds(new TextElement { X = 0, Y = 0 }), "the rest keep the video's own times");
		Assert.AreEqual(5, withCard.IntroShiftSeconds(new ProfileChartElement { X = 0, Y = 0, LayerId = nameof(OverlayElementType.Watermark) }), "the profile that follows the watermark");
		Assert.AreEqual(0, withCard.IntroShiftSeconds(new ProfileChartElement { X = 0, Y = 0 }), "a profile of its own timing");
	}

	[TestMethod]
	public void Span_IsTheCardsEnd_AndWhereTheWidgetsFadeIn()
	{
		using OverlayRenderer withCard = Renderer(true);
		using OverlayRenderer without = Renderer(false);

		Assert.IsNull(without.RouteIntroSpan);
		(double fadeFrom, double end) = withCard.RouteIntroSpan!.Value;
		Assert.AreEqual(5, end);
		Assert.IsTrue(fadeFrom is > 0 and < 5);
	}
}
