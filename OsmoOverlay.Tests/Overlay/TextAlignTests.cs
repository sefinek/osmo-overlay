using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Drones.Dji;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class TextAlignTests
{
	private const int Width = 1280;
	private const int Height = 720;
	// The anchor in pixels - the 4K reference space is three times the picture.
	private const int Anchor = 100;

	private static List<DerivedFrame> Frames(TelemetryFrame? raw = null)
	{
		TelemetryFrame frame = raw ?? new TelemetryFrame(0, 0, 50, 20, 100, null, 0, 0, 1);
		return TelemetryProcessor.Process([frame, frame with { FrameNumber = 1, SampleTimeSeconds = 1 }], null);
	}

	private static byte[] Draw(OverlayElement element, List<DerivedFrame> frames)
	{
		using var renderer = new OverlayRenderer(Width, Height, 0, [element], frames, 30, false);
		byte[] buffer = new byte[renderer.FrameBufferSize()];
		renderer.RenderInto(frames[0], buffer, premultiplied: true);
		return buffer;
	}

	/// <summary>The leftmost and rightmost column with ink in the rows from..to.</summary>
	private static (int Left, int Right) Ink(byte[] buffer, int from, int to)
	{
		int left = int.MaxValue, right = int.MinValue;
		for (int y = from; y <= to; y++)
		for (int x = 0; x < Width; x++)
		{
			if (buffer[(y * Width + x) * 4 + 3] < 64) continue;
			left = Math.Min(left, x);
			right = Math.Max(right, x);
		}

		return (left, right);
	}

	private static byte MaxAlpha(byte[] buffer)
	{
		byte max = 0;
		for (int i = 3; i < buffer.Length; i += 4) max = Math.Max(max, buffer[i]);
		return max;
	}

	[TestMethod]
	[DataRow(TextAlign.Left)]
	[DataRow(TextAlign.Center)]
	[DataRow(TextAlign.Right)]
	public void AStatsCaptionAndReadout_LineUpOnTheChosenSide(TextAlign align)
	{
		// A long caption over a short readout: the two lines only meet on the chosen side.
		var element = new HomeDistanceElement { X = Anchor * 3, Y = Anchor * 3, Label = "A VERY LONG CAPTION HERE", TextAlign = align, ShadowEnabled = false };
		byte[] buffer = Draw(element, Frames(new TelemetryFrame(0, 0, 50, 20, 100, null, 0, 0, 1, DistanceFromHomeMeters: 12)));

		(int labelLeft, int labelRight) = Ink(buffer, Anchor - 14, Anchor + 1);
		(int valueLeft, int valueRight) = Ink(buffer, Anchor + 8, Anchor + 32);

		Assert.IsTrue(valueRight - valueLeft < (labelRight - labelLeft) / 2, "the readout is the short one");
		switch (align)
		{
			case TextAlign.Left:
				Assert.AreEqual(labelLeft, valueLeft, 4);
				break;
			case TextAlign.Center:
				Assert.AreEqual((labelLeft + labelRight) / 2.0, (valueLeft + valueRight) / 2.0, 4);
				break;
			default:
				Assert.AreEqual(labelRight, valueRight, 4);
				break;
		}
	}

	[TestMethod]
	public void ARightAlignedText_EndsAtTheWidgetsRightEdgeWhateverItsLines()
	{
		var text = new TextElement { X = Anchor * 3, Y = Anchor * 3, Text = "short\na much longer line", TextAlign = TextAlign.Right, ShadowEnabled = false };
		byte[] buffer = Draw(text, Frames());
		byte[] left = Draw(text with { TextAlign = TextAlign.Left }, Frames());

		(int firstLeft, int firstRight) = Ink(buffer, Anchor - 12, Anchor + 4);
		(int secondLeft, int secondRight) = Ink(buffer, Anchor + 14, Anchor + 28);
		(int leftFirst, _) = Ink(left, Anchor - 12, Anchor + 4);
		Assert.AreEqual(secondRight, firstRight, 4, "both lines end together");
		Assert.IsTrue(firstLeft > secondLeft + 10, "the short line starts later");
		Assert.IsTrue(firstLeft > leftFirst, "moved from the left edge");
	}

	[TestMethod]
	public void OnlyTheWidgetsWithLinesToLineUpSupportIt()
	{
		Assert.IsTrue(OverlayElementBounds.SupportsTextAlign(new HomeDistanceElement { X = 0, Y = 0 }));
		Assert.IsTrue(OverlayElementBounds.SupportsTextAlign(new TextElement { X = 0, Y = 0 }));
		Assert.IsTrue(OverlayElementBounds.SupportsTextAlign(new FlightModeElement { X = 0, Y = 0 }));
		Assert.IsFalse(OverlayElementBounds.SupportsTextAlign(new SatellitesElement { X = 0, Y = 0 }), "its bars follow the value");
		Assert.IsFalse(OverlayElementBounds.SupportsTextAlign(new SpeedGaugeElement { X = 0, Y = 0 }));
	}

	[TestMethod]
	public void Opacity_FadesTheWholeWidget()
	{
		var element = new DistanceElement { X = Anchor * 3, Y = Anchor * 3, ShadowEnabled = false };
		List<DerivedFrame> frames = Frames();

		int full = MaxAlpha(Draw(element, frames)), half = MaxAlpha(Draw(element with { Opacity = 0.5f }, frames));
		Assert.IsTrue(full > 200, "drawn");
		Assert.AreEqual(full / 2.0, half, 6);
		Assert.AreEqual(0, MaxAlpha(Draw(element with { Opacity = 0 }, frames)));
	}

	[TestMethod]
	public void TheSignalPercentsTakeTheirBarsColors_UntilSwitchedOff()
	{
		var element = new SignalElement { X = Anchor * 3, Y = Anchor * 3, ShowBars = false, ShadowEnabled = false };
		List<DerivedFrame> frames = Frames(new TelemetryFrame(0, 0, 50, 20, 100, null, 0, 0, 1, DroneSignal: new DroneSignal(95, 30)));

		CollectionAssert.AreNotEqual(Draw(element, frames), Draw(element with { ColorByLevel = false }, frames));
	}

	[TestMethod]
	public void TheSignalLevelsAreTheElementsOwn()
	{
		var element = new SignalElement { X = Anchor * 3, Y = Anchor * 3, ShowBars = false, ShadowEnabled = false };
		List<DerivedFrame> frames = Frames(new TelemetryFrame(0, 0, 50, 20, 100, null, 0, 0, 1, DroneSignal: new DroneSignal(90, 90)));

		CollectionAssert.AreNotEqual(Draw(element, frames), Draw(element with { GoodPercent = 95 }, frames), "90 % stops being good");
		CollectionAssert.AreNotEqual(Draw(element with { GoodPercent = 95 }, frames), Draw(element with { GoodPercent = 95, FairColor = "#00FFFF" }, frames), "and takes the fair color");
		CollectionAssert.AreEqual(Draw(element, frames), Draw(element with { FairColor = "#00FFFF" }, frames), "a good link doesn't read the fair color");
	}

	[TestMethod]
	public void TheBatteryKeepsItsAccentWhenNotColoredByCharge()
	{
		var battery = new DroneBatteryElement { X = 0, Y = 0, AccentColor = "#112233", ColorByCharge = false };

		Assert.AreEqual(SkiaSharp.SKColor.Parse("#112233"), OverlayRenderer.DroneBatteryColor(battery, 5));
		Assert.AreNotEqual(SkiaSharp.SKColor.Parse("#112233"), OverlayRenderer.DroneBatteryColor(battery with { ColorByCharge = true }, 5));
	}
	[TestMethod]
	public void TheSignalAndTheGpsWidgetsEndOnTheSameLine()
	{
		List<DerivedFrame> frames = Frames(new TelemetryFrame(0, 0, 50, 20, 100, null, 0, 0, 1, DroneSignal: new DroneSignal(90, 70), GpsLevel: 5, GpsSatellites: 22));
		byte[] signal = Draw(new SignalElement { X = Anchor * 3, Y = Anchor * 3, ShadowEnabled = false }, frames);
		byte[] gps = Draw(new SatellitesElement { X = Anchor * 3, Y = Anchor * 3, ShadowEnabled = false }, frames);

		Assert.AreEqual(LowestInk(gps), LowestInk(signal), 2, "the bars and the last line sit on the count's baseline");
	}

	private static int LowestInk(byte[] buffer)
	{
		for (int y = Height - 1; y >= 0; y--)
		for (int x = 0; x < Width; x++)
			if (buffer[(y * Width + x) * 4 + 3] >= 64) return y;

		return -1;
	}
	[TestMethod]
	public void TheBatterysPercentAndVoltageAreCenteredOnItsIcon()
	{
		List<DerivedFrame> frames = Frames(new TelemetryFrame(0, 0, 50, 20, 100, null, 0, 0, 1, DroneBattery: new DroneBattery(34, 10.7, 5, 30, [3.56, 3.57, 3.57])));
		byte[] buffer = Draw(new DroneBatteryElement { X = Anchor * 3, Y = Anchor * 3, ShadowEnabled = false }, frames);

		(double iconMiddle, double textMiddle) = (Middle(buffer, Anchor - 2, Anchor + 18), Middle(buffer, Anchor + 24, Width - 1));
		Assert.AreEqual(iconMiddle, textMiddle, 3, "the digits and the voltage line as a pair about the icon's middle");
	}

	/// <summary>The middle row of the ink in the columns from..to, below the caption.</summary>
	private static double Middle(byte[] buffer, int from, int to)
	{
		int top = int.MaxValue, bottom = -1;
		for (int y = Anchor + 3; y < Height; y++)
		for (int x = from; x <= to; x++)
		{
			if (buffer[(y * Width + x) * 4 + 3] < 64) continue;
			top = Math.Min(top, y);
			bottom = Math.Max(bottom, y);
		}

		return (top + bottom) / 2.0;
	}
}
