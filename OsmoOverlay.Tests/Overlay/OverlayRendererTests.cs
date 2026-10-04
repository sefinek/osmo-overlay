using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class OverlayRendererTests
{
	private const int Width = 640;
	private const int Height = 360;

	private static List<DerivedFrame> Route(int count, double eastOffset)
	{
		List<DerivedFrame> frames = [];
		double distance = 0;
		for (int i = 0; i < count; i++)
		{
			double east = eastOffset + 200 * Math.Sin(i * 0.02);
			double north = i * 1.5;
			if (i > 0) distance += 3;
			var raw = new TelemetryFrame(i, i / 30.0, 50 + north / 111320.0, 20 + east / 71560.0, 200 + i * 0.1, null, 0, 0, 1);
			frames.Add(new DerivedFrame(raw, 20 + i % 30, i % 360, 2, distance, 0, 0, new SunPosition(120, 30), east, north, 1, 0, 0));
		}

		return frames;
	}

	private static byte[] Render(OverlayRenderer renderer, IReadOnlyList<DerivedFrame> frames)
	{
		byte[] buffer = new byte[renderer.FrameBufferSize()];
		// Sequentially up to the frame compared, so the trail builds the way it does in playback.
		foreach (DerivedFrame frame in frames.Take(150)) renderer.RenderInto(frame, buffer, premultiplied: true);
		return buffer;
	}

	private static OverlayRenderer Create(IReadOnlyList<DerivedFrame> frames)
	{
		List<OverlayElement> layout =
		[
			new CompassElement { X = 160, Y = 180 },
			new DistanceElement { X = 360, Y = 60 },
			new TripProgressBarElement { X = 320, Y = 330 },
			new SpeedGaugeElement { X = 520, Y = 200 }
		];
		return new OverlayRenderer(Width, Height, frames[0].Raw.AltitudeMeters, layout, frames,
			TelemetryProcessor.Summarize(frames).MaxSpeedKmh, false);
	}

	[TestMethod]
	public void MeasureElement_FramesWhatTheWidgetDraws()
	{
		List<DerivedFrame> frames = Route(100, 0);
		using OverlayRenderer renderer = Create(frames);

		SKRect? bar = renderer.MeasureElement(new TripProgressBarElement { X = 0, Y = 0 }, frames[50]);
		SKRect? gauge = renderer.MeasureElement(new SpeedGaugeElement { X = 0, Y = 0 }, frames[50]);
		SKRect? noImage = renderer.MeasureElement(new ImageElement { X = 0, Y = 0 }, frames[50]);

		Assert.IsNotNull(bar);
		// The percentage and distance left sit above the bar itself - outside its ProgressBarHeight track.
		Assert.IsTrue(bar.Value.Top < -OverlayElementBounds.ProgressBarHeight / 2, $"top {bar.Value.Top}");
		Assert.IsTrue(bar.Value.Width < 100_000, "trimmed to the drawing, not the recording area");
		Assert.IsNotNull(gauge);
		Assert.AreEqual(0, gauge.Value.MidX, OverlayElementBounds.SpeedRadius * 0.2, "a round gauge is drawn around its anchor");
		Assert.IsNull(noImage, "an image with no file draws nothing");
	}

	[TestMethod]
	public void MeasureElement_RoundWidgetFitsItsDrawing()
	{
		List<DerivedFrame> frames = Route(100, 0);
		using OverlayRenderer renderer = Create(frames);

		SKRect gauge = renderer.MeasureElement(new SpeedGaugeElement { X = 0, Y = 0 }, frames[50])!.Value;

		Assert.IsTrue(gauge.Width < OverlayElementBounds.SpeedRadius * 2 + 20 + 2, $"{gauge}");
	}

	[TestMethod]
	public void SetFrames_DrawsLikeARendererBuiltForThoseFrames()
	{
		List<DerivedFrame> before = Route(300, 0);
		List<DerivedFrame> after = Route(200, 50);

		using OverlayRenderer fresh = Create(after);
		using OverlayRenderer swapped = Create(before);
		Render(swapped, before);
		swapped.SetFrames(after, after[0].Raw.AltitudeMeters, TelemetryProcessor.Summarize(after).MaxSpeedKmh);

		CollectionAssert.AreEqual(Render(fresh, after), Render(swapped, after));
	}

	[TestMethod]
	public void ParallelWidgets_DrawLikeWidgetsDrawnStraightOn()
	{
		List<DerivedFrame> frames = Route(300, 0);
		// Every widget, the first one fading in at the frames compared - its layer is bounded by what it draws.
		List<OverlayElement> layout =
		[
			.. OverlayPreset.CreateDefault("p", "P").Elements
				.Where(e => e is not ImageElement)
				.Select((e, i) => e with
				{
					Visible = true, AppearAtSeconds = i == 0 ? 4.8 : null, AnimationType = i == 0 ? OverlayAnimationType.SlideUp : OverlayAnimationType.None
				})
		];

		byte[] RenderWith(bool parallel)
		{
			using var renderer = new OverlayRenderer(Width, Height, frames[0].Raw.AltitudeMeters, layout, frames,
				TelemetryProcessor.Summarize(frames).MaxSpeedKmh) { ParallelWidgets = parallel };
			return Render(renderer, frames);
		}

		byte[] straight = RenderWith(false);
		byte[] parallel = RenderWith(true);

		int maxDifference = straight.Zip(parallel, (a, b) => Math.Abs(a - b)).Max();
		Assert.IsTrue(maxDifference <= 2, $"differs by up to {maxDifference}");
		Assert.IsTrue(straight.Any(b => b != 0));
	}

	[TestMethod]
	public void Trail_DrawsTheSameWhicheverFramesCameBefore()
	{
		List<DerivedFrame> frames = Route(300, 0);
		using OverlayRenderer sequential = Create(frames);
		using OverlayRenderer skipping = Create(frames);
		using OverlayRenderer rewound = Create(frames);

		byte[] expected = Render(sequential, frames);
		byte[] skipped = new byte[skipping.FrameBufferSize()];
		for (int i = 0; i < 150; i += 7) skipping.RenderInto(frames[i], skipped, premultiplied: true);
		skipping.RenderInto(frames[149], skipped, premultiplied: true);
		byte[] back = new byte[rewound.FrameBufferSize()];
		rewound.RenderInto(frames[280], back, premultiplied: true);
		rewound.RenderInto(frames[149], back, premultiplied: true);

		CollectionAssert.AreEqual(expected, skipped, "a forward jump extends the trail by the frames skipped");
		CollectionAssert.AreEqual(expected, back, "a jump back builds it again from the start");
	}

	[TestMethod]
	public void EveryWidgetType_DrawsSomething()
	{
		List<DerivedFrame> frames = Route(300, 0);
		string imagePath = Path.Combine(Path.GetTempPath(), $"osmooverlay-test-{Guid.NewGuid():N}.png");
		using (var bitmap = new SKBitmap(40, 20))
		{
			bitmap.Erase(SKColors.Red);
			using FileStream file = File.Create(imagePath);
			bitmap.Encode(file, SKEncodedImageFormat.Png, 100);
		}

		try
		{
			// The map needs tiles from the network, which a unit test doesn't fetch - its placeholder still draws.
			foreach (OverlayElement element in OverlayPreset.CreateDefault("test", "Test").Elements)
			{
				OverlayElement shown = (element is ImageElement image ? image with { ImagePath = imagePath } : element) with
				{
					Visible = true, AppearAtSeconds = null, AnimationType = OverlayAnimationType.None
				};
				if (shown is MapAttributionElement)
				{
					// The credit is drawn only with a map on screen; GUGiK's may be brief, so the map alone doesn't add one.
					var map = new MapWidgetElement { X = 1920, Y = 1080, MapProviderId = MapProviders.GugikId };
					CollectionAssert.AreNotEqual(RenderLayout([map]), RenderLayout([map, shown]), "MapAttribution drew nothing");
					continue;
				}

				Assert.IsTrue(RenderLayout([shown]).Any(b => b != 0), $"{element.Type} drew nothing");
			}
		}
		finally
		{
			File.Delete(imagePath);
		}

		byte[] RenderLayout(List<OverlayElement> layout)
		{
			using var renderer = new OverlayRenderer(Width, Height, frames[0].Raw.AltitudeMeters, layout, frames,
				TelemetryProcessor.Summarize(frames).MaxSpeedKmh, false);
			return Render(renderer, frames);
		}
	}

	[TestMethod]
	public void MapCredit_FollowsTheTermsOfTheMapsOnScreen()
	{
		List<DerivedFrame> frames = Route(300, 0);
		var eox = new MapWidgetElement { X = 1920, Y = 1080, MapProviderId = MapProviders.EoxId };
		MapWidgetElement gugik = eox with { MapProviderId = MapProviders.GugikId };
		var hiddenCredit = new MapAttributionElement { X = 100, Y = 2000, Visible = false };

		// Without a mosaic both maps draw the same placeholder, so only the credit tells them apart.
		CollectionAssert.AreNotEqual(RenderLayout([gugik]), RenderLayout([eox]), "a required credit without a credit widget");
		CollectionAssert.AreNotEqual(RenderLayout([gugik, hiddenCredit]), RenderLayout([eox, hiddenCredit]), "a required credit hidden");
		CollectionAssert.AreEqual(RenderLayout([gugik]), RenderLayout([gugik, hiddenCredit]), "a brief credit hidden");
		Assert.IsFalse(RenderLayout([hiddenCredit with { Visible = true }]).Any(b => b != 0), "a credit without a map");

		byte[] RenderLayout(List<OverlayElement> layout)
		{
			using var renderer = new OverlayRenderer(Width, Height, frames[0].Raw.AltitudeMeters, layout, frames,
				TelemetryProcessor.Summarize(frames).MaxSpeedKmh, false);
			return Render(renderer, frames);
		}
	}

	[TestMethod]
	public void TripProgressBar_WithASpeedGate_WaitsForTheRideToReachIt()
	{
		// Speeds 20-49 km/h: 45 is first reached at frame 25, 60 never.
		List<DerivedFrame> frames = Route(300, 0);
		var bar = new TripProgressBarElement { X = 320, Y = 330 };

		Assert.IsTrue(DrawsAt(bar with { ShowFromSpeedKmh = null }, 10));
		Assert.IsFalse(DrawsAt(bar with { ShowFromSpeedKmh = 45 }, 10), "before the speed is reached");
		Assert.IsTrue(DrawsAt(bar with { ShowFromSpeedKmh = 45 }, 200), "after it");
		Assert.IsTrue(DrawsAt(bar with { ShowFromSpeedKmh = 45, AppearAtSeconds = 9 }, 200), "the gate takes the appear time's place");
		Assert.IsFalse(DrawsAt(bar with { ShowFromSpeedKmh = 60 }, 299), "a speed never reached");

		bool DrawsAt(OverlayElement element, int frame)
		{
			using var renderer = new OverlayRenderer(Width, Height, frames[0].Raw.AltitudeMeters, [element], frames,
				TelemetryProcessor.Summarize(frames).MaxSpeedKmh, false);
			byte[] buffer = new byte[renderer.FrameBufferSize()];
			renderer.RenderInto(frames[frame], buffer, premultiplied: true);
			return buffer.Any(b => b != 0);
		}
	}

	[TestMethod]
	public void ProfileChart_MovesWithTheRide()
	{
		List<DerivedFrame> frames = Route(300, 0);
		using var renderer = new OverlayRenderer(Width, Height, frames[0].Raw.AltitudeMeters,
			[new ProfileChartElement { X = 20, Y = 40 }], frames, TelemetryProcessor.Summarize(frames).MaxSpeedKmh, false);

		byte[] early = new byte[renderer.FrameBufferSize()];
		byte[] late = new byte[renderer.FrameBufferSize()];
		renderer.RenderInto(frames[10], early, premultiplied: true);
		renderer.RenderInto(frames[250], late, premultiplied: true);

		CollectionAssert.AreNotEqual(early, late);
	}

	[TestMethod]
	public void MapMosaicCoversRoute_WithoutAMosaic_IsTrue()
	{
		using OverlayRenderer renderer = Create(Route(10, 0));

		Assert.IsTrue(renderer.MapMosaicCoversRoute);
	}
}
