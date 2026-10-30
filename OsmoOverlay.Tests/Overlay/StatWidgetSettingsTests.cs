using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class StatWidgetSettingsTests
{
	private const int Width = 1280;
	private const int Height = 720;
	private const int Anchor = 300;

	private static List<DerivedFrame> Frames(TelemetryFrame raw)
	{
		return TelemetryProcessor.Process([raw, raw with { FrameNumber = 1, SampleTimeSeconds = 1 }], null);
	}

	private static TelemetryFrame Flight(double height = 60, double away = 200, int satellites = 20, double climb = 0, string mode = "P-GPS")
	{
		return new TelemetryFrame(0, 0, 50, 20, 100, null, 0, 0, 1, HeightAboveHomeMeters: height, DistanceFromHomeMeters: away, GpsSatellites: satellites, VerticalSpeedMs: climb, FlightMode: mode);
	}

	private static byte[] Draw(OverlayElement element, List<DerivedFrame> frames)
	{
		using var renderer = new OverlayRenderer(Width, Height, 0, [element], frames, 30, false);
		byte[] buffer = new byte[renderer.FrameBufferSize()];
		renderer.RenderInto(frames[0], buffer, premultiplied: true);
		return buffer;
	}

	private static (int Top, int Right) Ink(byte[] buffer)
	{
		int top = int.MaxValue, right = -1;
		for (int y = 0; y < Height; y++)
		for (int x = 0; x < Width; x++)
		{
			if (buffer[(y * Width + x) * 4 + 3] < 64) continue;
			top = Math.Min(top, y);
			right = Math.Max(right, x);
		}

		return (top, right);
	}

	[TestMethod]
	public void TheDigitsAfterThePointAreTheWidgetsOwnWhenSet()
	{
		Assert.AreEqual("+1.2", OverlayRenderer.FormatVerticalSpeed(1.234, UnitSystem.Metric).Value);
		Assert.AreEqual("+1", OverlayRenderer.FormatVerticalSpeed(1.234, UnitSystem.Metric, 0).Value);
		Assert.AreEqual("-1.23", OverlayRenderer.FormatVerticalSpeed(-1.234, UnitSystem.Metric, 2).Value);
		Assert.AreEqual("21.46", OverlayRenderer.FormatTemperature(21.456, UnitSystem.Metric, 2).Value);
		Assert.IsTrue(OverlayElementBounds.SupportsDecimals(new HeightElement { X = 0, Y = 0 }));
		Assert.IsFalse(OverlayElementBounds.SupportsDecimals(new DeviceBatteryElement { X = 0, Y = 0 }), "a percent is whole");
	}

	[TestMethod]
	public void TheCaptionAndTheUnitCanBeLeftOut()
	{
		var element = new HeightElement { X = Anchor * 3, Y = Anchor * 3, ShadowEnabled = false };
		List<DerivedFrame> frames = Frames(Flight());

		(int top, int right) = Ink(Draw(element, frames));
		Assert.IsTrue(Ink(Draw(element with { ShowLabel = false }, frames)).Top > top + 6, "no caption over the value");
		Assert.IsTrue(Ink(Draw(element with { ShowUnit = false }, frames)).Right < right - 6, "no unit after it");
		CollectionAssert.AreNotEqual(Draw(element, frames), Draw(element with { UppercaseLabel = false }, frames));
	}

	[TestMethod]
	public void AHeightOrADistanceTurnsColorOnlyPastItsLimits()
	{
		List<DerivedFrame> frames = Frames(Flight(height: 130, away: 700));
		var height = new HeightElement { X = Anchor * 3, Y = Anchor * 3, ShadowEnabled = false };
		var away = new HomeDistanceElement { X = Anchor * 3, Y = Anchor * 3, ShadowEnabled = false };

		CollectionAssert.AreEqual(Draw(height, frames), Draw(height with { WarnAboveMeters = 150 }, frames), "under the limit");
		CollectionAssert.AreNotEqual(Draw(height, frames), Draw(height with { WarnAboveMeters = 120 }, frames), "over it");
		CollectionAssert.AreNotEqual(Draw(height with { WarnAboveMeters = 120 }, frames), Draw(height with { WarnAboveMeters = 120, AlertAboveMeters = 125 }, frames), "the alert limit comes first");
		CollectionAssert.AreNotEqual(Draw(away, frames), Draw(away with { AlertAboveMeters = 500 }, frames));
	}

	[TestMethod]
	public void TheSatellitesTheModeAndTheClimbRateTakeTheirColors()
	{
		var gps = new SatellitesElement { X = Anchor * 3, Y = Anchor * 3, ShadowEnabled = false, ShowBars = false };
		List<DerivedFrame> few = Frames(Flight(satellites: 4)), many = Frames(Flight(satellites: 20));
		CollectionAssert.AreNotEqual(Draw(gps, few), Draw(gps with { ColorByCount = false }, few), "four satellites is an alert");
		CollectionAssert.AreEqual(Draw(gps, many), Draw(gps with { ColorByCount = false }, many), "twenty is fine");

		var mode = new FlightModeElement { X = Anchor * 3, Y = Anchor * 3, ShadowEnabled = false };
		List<DerivedFrame> atti = Frames(Flight(mode: "ATTI")), normal = Frames(Flight());
		CollectionAssert.AreNotEqual(Draw(mode, atti), Draw(mode with { ColorByMode = false }, atti));
		CollectionAssert.AreEqual(Draw(mode, normal), Draw(mode with { ColorByMode = false }, normal));

		var vertical = new VerticalSpeedElement { X = Anchor * 3, Y = Anchor * 3, ShadowEnabled = false };
		List<DerivedFrame> climbing = Frames(Flight(climb: 3)), still = Frames(Flight());
		CollectionAssert.AreNotEqual(Draw(vertical, climbing), Draw(vertical with { ClimbColor = "#00FF00" }, climbing));
		CollectionAssert.AreEqual(Draw(vertical, still), Draw(vertical with { ClimbColor = "#00FF00" }, still), "standing still is neither");
	}

	[TestMethod]
	public void TheChartCanDropItsMarkerAndShowItsRange()
	{
		List<TelemetryFrame> raw = [.. Enumerable.Range(0, 60).Select(i => new TelemetryFrame(i, i, 50 + i * 1e-5, 20, 100, null, 0, 0, 1, HeightAboveHomeMeters: 20 + i))];
		List<DerivedFrame> frames = TelemetryProcessor.Process(raw, null);
		var chart = new ProfileChartElement { X = Anchor * 3, Y = Anchor * 3, Series = ProfileSeries.Height, Axis = ProfileAxis.Time, ShadowEnabled = false };

		CollectionAssert.AreNotEqual(Draw(chart, frames), Draw(chart with { ShowMarker = false }, frames));
		CollectionAssert.AreNotEqual(Draw(chart, frames), Draw(chart with { ShowMinMax = true }, frames));
		CollectionAssert.AreNotEqual(Draw(chart, frames), Draw(chart with { LineWidth = 2 }, frames));
	}
	[TestMethod]
	public void TheRecordHeightAndDistanceFromTheTakeOffAreRunningMaxima()
	{
		List<TelemetryFrame> raw = [.. new[] { 10.0, 80, 120, 90, 40 }.Select((h, i) => new TelemetryFrame(i, i, 50, 20, 100, null, 0, 0, 1, HeightAboveHomeMeters: h, DistanceFromHomeMeters: h * 5))];
		TripStats stats = TripStats.Compute(TelemetryProcessor.Process(raw, null));

		Assert.AreEqual(80, stats.MaxHeightMeters(1));
		Assert.AreEqual(120, stats.MaxHeightMeters(4), "it stays as the aircraft comes down");
		Assert.AreEqual(600, stats.MaxHomeDistanceMeters(4));
		Assert.AreEqual(0, TripStats.Compute(Frames(new TelemetryFrame(0, 0, 50, 20, 100, null, 0, 0, 1))).MaxHeightMeters(0), "no flight log, no record");
	}

	[TestMethod]
	public void TheFpvPresetShowsTheRecordsAndNoSecondHeight()
	{
		var fpv = OverlayPreset.CreateFpv("y", "FPV");

		Assert.IsFalse(fpv.Elements.Single(e => e.Type == OverlayElementType.Elevation).Visible, "the sea-level height is a second \"height\"");
		foreach (TripStatKind kind in new[] { TripStatKind.MaxHeight, TripStatKind.MaxHomeDistance })
			Assert.IsTrue(fpv.Elements.OfType<TripStatElement>().Single(e => e.Stat == kind).Visible, kind.ToString());
	}
}
