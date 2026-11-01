using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class ProfileChartTests
{
	private static List<DerivedFrame> Flight(Func<int, double> height)
	{
		List<TelemetryFrame> raw = [.. Enumerable.Range(0, 60).Select(i => new TelemetryFrame(i, i, 50 + i * 1e-5, 20, 100, null, 0, 0, 1, HeightAboveHomeMeters: height(i)))];
		return TelemetryProcessor.Process(raw, null);
	}

	private static SKRect Measure(ProfileChartElement chart, List<DerivedFrame> frames)
	{
		using var renderer = new OverlayRenderer((int)OverlayElementBounds.ReferenceWidth, (int)OverlayElementBounds.ReferenceHeight, 0, [chart], frames, 30, false);
		SKRect box = renderer.MeasureElement(chart, frames[10])!.Value;
		box.Offset(chart.X, chart.Y);
		return box;
	}

	[TestMethod]
	public void AutoHeight_MakesTheChartLowerForASmallChange_AndKeepsItsBottomEdge()
	{
		var chart = new ProfileChartElement { X = 100, Y = 1000, Series = ProfileSeries.Height, Axis = ProfileAxis.Time, ShadowEnabled = false };
		List<DerivedFrame> flat = Flight(i => 100 + (i % 7) * 0.5), climb = Flight(i => i * 3);

		SKRect fixedBox = Measure(chart, flat);
		SKRect fitted = Measure(chart with { AutoHeight = true }, flat);
		Assert.IsTrue(fitted.Height < fixedBox.Height - 60, $"{fitted.Height} against {fixedBox.Height}");
		Assert.AreEqual(fixedBox.Bottom, fitted.Bottom, 1, "the bottom edge stays");
		Assert.AreEqual(fixedBox.Height, Measure(chart with { AutoHeight = true }, climb).Height, 2, "a real climb fills the panel");
	}
}
