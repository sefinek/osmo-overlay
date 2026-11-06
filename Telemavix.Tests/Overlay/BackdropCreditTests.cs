using Telemavix.Core.Mapping;
using Telemavix.Core.Overlay;
using Telemavix.Core.Telemetry;
using SkiaSharp;

namespace Telemavix.Tests.Overlay;

[TestClass]
public sealed class BackdropCreditTests
{
	private const int Width = 1280, Height = 720;

	private static bool HasInk(byte[] bgra, int left, int top, int right, int bottom)
	{
		for (int y = top; y < bottom; y++)
		{
			for (int x = left; x < right; x++)
			{
				if (bgra[(y * Width + x) * 4 + 3] != 0) return true;
			}
		}

		return false;
	}

	private static byte[] Draw(params OverlayElement[] layout)
	{
		List<DerivedFrame> frames = TelemetryProcessor.Process([new TelemetryFrame(0, 0, 50, 20, 100, null, 0, 0, 1)], null);
		using var renderer = new OverlayRenderer(Width, Height, 0, layout, frames, showWatermark: false)
		{
			Backdrop = new BackdropMap(MapProviders.EsriPublicId, true)
		};
		byte[] bgra = new byte[Width * Height * 4];
		renderer.RenderInto(frames[0], bgra);
		return bgra;
	}

	[TestMethod]
	public void TheBackdropCreditWidgetCreditsTheBackdropWhereItIsPlaced()
	{
		byte[] bgra = Draw(new MapAttributionElement { X = 12, Y = 2100, Scale = 0.8f, Id = "first" },
			new MapAttributionElement { X = 1500, Y = 1000, Scale = 0.8f, Id = OverlayPreset.BackdropCreditId });

		Assert.IsTrue(HasInk(bgra, 450, 300, 750, 360), "the backdrop's credit is where the second widget is");
		Assert.IsFalse(HasInk(bgra, 700, 650, Width, Height), "and not at the corner");
	}

	[TestMethod]
	public void TheBackdropCreditWidgetIsMeasuredWithTheBackdropsOwnCredit()
	{
		List<DerivedFrame> frames = TelemetryProcessor.Process([new TelemetryFrame(0, 0, 50, 20, 100, null, 0, 0, 1)], null);
		MapAttributionElement widget = new() { X = 12, Y = 2100, Id = OverlayPreset.BackdropCreditId };
		SKRect Measure(BackdropMap? backdrop)
		{
			using var renderer = new OverlayRenderer(Width, Height, 0, [widget], frames, showWatermark: false) { Backdrop = backdrop };
			return renderer.MeasureElement(widget, frames[0])!.Value;
		}

		SKRect withBackdrop = Measure(new BackdropMap(MapProviders.EsriPublicId, true)), without = Measure(null);

		Assert.IsGreaterThan(without.Width, withBackdrop.Width, "the backdrop's credit is not the sample's");
	}

	[TestMethod]
	public void WithOneCreditWidgetTheBackdropIsCreditedAtTheBottomRight()
	{
		byte[] bgra = Draw(new MapAttributionElement { X = 12, Y = 2100, Scale = 0.8f, Id = "first" });

		Assert.IsTrue(HasInk(bgra, 700, 650, Width, Height));
	}
}
