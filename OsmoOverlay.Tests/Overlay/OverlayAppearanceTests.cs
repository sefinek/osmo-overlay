using System.Text.Json;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class OverlayAppearanceTests
{
	private const int Width = 640;
	private const int Height = 360;

	private static List<DerivedFrame> Frames()
	{
		List<DerivedFrame> frames = [];
		for (int i = 0; i < 60; i++)
		{
			var raw = new TelemetryFrame(i, i / 30.0, 50, 20, 200, null, 0, 0, 1);
			frames.Add(new DerivedFrame(raw, 20, 0, 2, i, 0, 0, new SunPosition(120, 30), 0, 0, 1, 0, 0));
		}

		return frames;
	}

	private static byte[] Render(OverlayElement element)
	{
		List<DerivedFrame> frames = Frames();
		using var renderer = new OverlayRenderer(Width, Height, frames[0].Raw.AltitudeMeters, [element], frames, TelemetryProcessor.Summarize(frames).MaxSpeedKmh, false);
		byte[] buffer = new byte[renderer.FrameBufferSize()];
		renderer.RenderInto(frames[10], buffer, premultiplied: true);
		return buffer;
	}

	private static byte AlphaAt(byte[] buffer, float x, float y)
	{
		return buffer[((int)y * Width + (int)x) * 4 + 3];
	}

	[TestMethod]
	public void Panel_IsFilledAtItsOpacityAndTheShadowStaysOutsideIt()
	{
		float scale = OverlayElementBounds.GetScale(Width, Height);
		(float cx, float cy) = OverlayElementBounds.ToPixels(1920, 1080, Width, Height);
		float radius = OverlayElementBounds.TiltRadius * scale;

		RollGaugeElement Roll(float opacity)
		{
			return new RollGaugeElement { X = 1920, Y = 1080, ShadowEnabled = true, ShadowRadius = 45, ShadowOpacity = 1f, PanelOpacity = opacity };
		}

		byte[] half = Render(Roll(0.5f));
		byte[] none = Render(Roll(0f));

		Assert.AreEqual(128, AlphaAt(half, cx, cy - radius * 0.5f), 3, "the panel's own fill");
		Assert.AreEqual(0, AlphaAt(none, cx, cy - radius * 0.5f), 2, "no panel and no shadow under the widget");
		Assert.IsTrue(AlphaAt(none, cx, cy - radius - 2) > 15, "the shadow outside it");
	}

	[TestMethod]
	public void SpeedGauge_FixedTopOfTheScaleMovesTheNeedle()
	{
		byte[] low = Render(new SpeedGaugeElement { X = 1920, Y = 1080, MaxSpeed = 40 });
		byte[] high = Render(new SpeedGaugeElement { X = 1920, Y = 1080, MaxSpeed = 200 });

		CollectionAssert.AreNotEqual(low, high);
	}

	[TestMethod]
	public void Element_KeepsItsShadowPanelAndScaleInJson()
	{
		OverlayElement original = new SpeedGaugeElement
		{
			X = 10, Y = 20, Id = "speed", ShadowEnabled = false, ShadowColor = "#102030", ShadowOpacity = 0.3f, ShadowRadius = 5, ShadowOffsetX = 2, ShadowOffsetY = -3,
			PanelColor = "#405060", PanelOpacity = 0.7f, MaxSpeed = 80
		};

		var read = (SpeedGaugeElement)JsonSerializer.Deserialize<OverlayElement>(JsonSerializer.Serialize(original))!;

		Assert.AreEqual(original, read);
	}

	[TestMethod]
	public void DefaultWidgets_HaveASoftShadowOn()
	{
		List<OverlayElement> elements = OverlayPreset.CreateDefault("test", "Test").Elements;

		Assert.IsTrue(elements.All(e => e.ShadowEnabled), "on by default");
		Assert.IsTrue(elements.All(e => e.ShadowRadius is > 0 and <= OverlayRenderer.ShadowRadiusLarge), "a soft edge, not a wide glow");
		Assert.AreEqual(140, Math.Round(OverlayRenderer.PanelOpacityDefault * 255));
	}
}
