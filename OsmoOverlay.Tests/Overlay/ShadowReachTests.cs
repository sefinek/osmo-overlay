using OsmoOverlay.Core.Overlay;
using SkiaSharp;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class ShadowReachTests
{
	private const int Width = 640;
	private const int Height = 360;

	[TestMethod]
	public void Shadow_ReachesEverySideOfARoundWidget()
	{
		List<DerivedFrame> frames = CreateFrames();

		var element = new SpeedGaugeElement { X = 1920, Y = 1080, ShadowRadius = 45, ShadowOpacity = 1f };
		using var renderer = new OverlayRenderer(Width, Height, 200, [element], frames, 30, false);
		byte[] buffer = new byte[renderer.FrameBufferSize()];
		renderer.RenderInto(frames[10], buffer, premultiplied: true);

		float scale = OverlayElementBounds.GetScale(Width, Height);
		(float cx, float cy) = OverlayElementBounds.ToPixels(1920, 1080, Width, Height);
		float edge = (OverlayElementBounds.SpeedRadius - 4) * scale + 2;
		byte[] sides =
		[
			Alpha(cx + edge, cy), Alpha(cx - edge, cy), Alpha(cx, cy - edge), Alpha(cx, cy + edge)
		];

		Assert.IsTrue(sides.All(a => a > 15), $"a side is cut off: {string.Join(", ", sides)}");
		Assert.IsTrue(sides.Max() - sides.Min() < 25, $"the sides differ: {string.Join(", ", sides)}");

		byte Alpha(float x, float y)
		{
			return buffer[((int)y * Width + (int)x) * 4 + 3];
		}
	}

	[TestMethod]
	public void ShadowOff_LeavesNothingOutsideTheWidget()
	{
		List<DerivedFrame> frames = CreateFrames();

		var element = new SpeedGaugeElement { X = 1920, Y = 1080, ShadowEnabled = false };
		using var renderer = new OverlayRenderer(Width, Height, 200, [element], frames, 30, false);
		byte[] buffer = new byte[renderer.FrameBufferSize()];
		renderer.RenderInto(frames[10], buffer, premultiplied: true);

		float scale = OverlayElementBounds.GetScale(Width, Height);
		(float cx, float cy) = OverlayElementBounds.ToPixels(1920, 1080, Width, Height);
		int outside = (int)((OverlayElementBounds.SpeedRadius - 4) * scale + 4);

		Assert.AreEqual(0, buffer[((int)cy * Width + (int)cx + outside) * 4 + 3]);
		Assert.AreEqual(0, buffer[(((int)cy - outside) * Width + (int)cx) * 4 + 3]);
		Assert.AreEqual(0, buffer[(((int)cy + outside) * Width + (int)cx) * 4 + 3]);
	}

	[TestMethod]
	public void Shadow_SurroundsATextWidget()
	{
		var shadowed = new TextElement { X = 1920, Y = 1080, ShadowRadius = OverlayRenderer.ShadowRadiusMax, ShadowOpacity = 1f };
		(byte[] withShadow, SKRect bounds) = RenderWithBounds(shadowed);
		byte[] withoutShadow = RenderWithBounds(shadowed with { ShadowOpacity = 0f }).Buffer;

		float scale = OverlayElementBounds.GetScale(Width, Height);
		(float cx, float cy) = OverlayElementBounds.ToPixels(1920, 1080, Width, Height);
		int x = (int)(cx + bounds.Left * scale) - 2;
		int y = (int)(cy + bounds.MidY * scale);

		Assert.IsTrue(withShadow[(y * Width + x) * 4 + 3] > 15, "no shadow beside the text");
		Assert.AreEqual(0, withoutShadow[(y * Width + x) * 4 + 3]);
	}

	private static (byte[] Buffer, SKRect Bounds) RenderWithBounds(OverlayElement element)
	{
		List<DerivedFrame> frames = CreateFrames();

		using var renderer = new OverlayRenderer(Width, Height, 200, [element], frames, 30, false);
		byte[] buffer = new byte[renderer.FrameBufferSize()];
		renderer.RenderInto(frames[10], buffer, premultiplied: true);
		return (buffer, renderer.MeasureElement(element, frames[10])!.Value);
	}

	private static List<DerivedFrame> CreateFrames()
	{
		List<DerivedFrame> frames = [];
		for (int i = 0; i < 60; i++)
		{
			var raw = new TelemetryFrame(i, i / 30.0, 50, 20, 200, null, 0, 0, 1);
			frames.Add(new DerivedFrame(raw, 20, 0, 2, i, 0, 0, new SunPosition(120, 30), 0, 0, 1, 0, 0));
		}

		return frames;
	}
}
