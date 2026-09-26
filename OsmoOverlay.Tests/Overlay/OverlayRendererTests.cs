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
		for (var i = 0; i < count; i++)
		{
			var east = eastOffset + 200 * Math.Sin(i * 0.02);
			var north = i * 1.5;
			if (i > 0) distance += 3;
			var raw = new TelemetryFrame(i, i / 30.0, 50 + north / 111320.0, 20 + east / 71560.0, 200 + i * 0.1, null, 0, 0, 1);
			frames.Add(new DerivedFrame(raw, 20 + i % 30, i % 360, 2, distance, 0, 0, new SunPosition(120, 30), east, north, 1));
		}

		return frames;
	}

	private static byte[] Render(OverlayRenderer renderer, IReadOnlyList<DerivedFrame> frames)
	{
		var buffer = new byte[renderer.FrameBufferSize()];
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
	public void EveryWidgetType_DrawsSomething()
	{
		List<DerivedFrame> frames = Route(300, 0);
		var imagePath = Path.Combine(Path.GetTempPath(), $"osmooverlay-test-{Guid.NewGuid():N}.png");
		using (var bitmap = new SKBitmap(40, 20))
		{
			bitmap.Erase(SKColors.Red);
			using FileStream file = File.Create(imagePath);
			bitmap.Encode(file, SKEncodedImageFormat.Png, 100);
		}

		try
		{
			// The map needs tiles from the network, which a unit test doesn't fetch - its placeholder still draws.
			foreach (OverlayElement element in OverlayPreset.CreateDefault("test", "Test", Width, Height).Elements)
			{
				OverlayElement shown = (element is ImageElement image ? image with { ImagePath = imagePath } : element) with
				{
					Visible = true, AppearAtSeconds = null, AnimationType = OverlayAnimationType.None
				};
				using var renderer = new OverlayRenderer(Width, Height, frames[0].Raw.AltitudeMeters, [shown], frames,
					TelemetryProcessor.Summarize(frames).MaxSpeedKmh, false);

				Assert.IsTrue(Render(renderer, frames).Any(b => b != 0), $"{element.Type} drew nothing");
			}
		}
		finally
		{
			File.Delete(imagePath);
		}
	}

	[TestMethod]
	public void ProfileChart_MovesWithTheRide()
	{
		List<DerivedFrame> frames = Route(300, 0);
		using var renderer = new OverlayRenderer(Width, Height, frames[0].Raw.AltitudeMeters,
			[new ProfileChartElement { X = 20, Y = 40 }], frames, TelemetryProcessor.Summarize(frames).MaxSpeedKmh, false);

		var early = new byte[renderer.FrameBufferSize()];
		var late = new byte[renderer.FrameBufferSize()];
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
