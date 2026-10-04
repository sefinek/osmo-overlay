using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class OverlayPresetTests
{
	private static OverlayElement Get(OverlayPreset preset, OverlayElementType type)
	{
		return preset.Elements.Single(e => e.Type == type);
	}

	[TestMethod]
	public void CreateDefault_At4K_PlacesWidgetsOnTheGuides()
	{
		var preset = OverlayPreset.CreateDefault("d", "Default");

		(OverlayElementType Type, float X, float Y)[] expected =
		[
			(OverlayElementType.DateTimeText, 70, 104),
			(OverlayElementType.Elevation, 69, 204.333f),
			(OverlayElementType.Gradient, 70, 424.333f),
			(OverlayElementType.Distance, 71, 644.333f),
			(OverlayElementType.SpeedGauge, 3512, 1832),
			(OverlayElementType.RollGauge, 3512, 1421),
			(OverlayElementType.MapWidget, 331.5f, 1828.5f),
			(OverlayElementType.Compass, 891.5f, 1828.5f),
			(OverlayElementType.GMeter, 3657, 183),
			(OverlayElementType.SunWidget, 3678, 482),
			(OverlayElementType.TripProgressBar, 1920, 2074)
		];

		foreach ((OverlayElementType type, float x, float y) in expected)
		{
			OverlayElement element = Get(preset, type);
			Assert.AreEqual(x, element.X, 0.01, $"{type} X");
			Assert.AreEqual(y, element.Y, 0.01, $"{type} Y");
		}
	}

	/// <summary>The shapes' drawn edges, measured - text is left out, its edges follow the system's font.</summary>
	[TestMethod]
	public void CreateDefault_At4K_DrawsShapesUpToTheMargin()
	{
		const float m = OverlayElementBounds.Margin;
		const float right = OverlayElementBounds.ReferenceWidth - m;
		const float bottom = OverlayElementBounds.ReferenceHeight - m;
		var preset = OverlayPreset.CreateDefault("d", "Default");
		List<DerivedFrame> frames = [];
		for (int i = 0; i < 30; i++)
			frames.Add(new DerivedFrame(new TelemetryFrame(i, i / 30.0, 50, 20, 200, null, 0, 0, 1), 20, 0, 0, i, 0, 0, new SunPosition(120, 30), 0, 0, 1, 0, 0));
		using var renderer = new OverlayRenderer((int)OverlayElementBounds.ReferenceWidth, (int)OverlayElementBounds.ReferenceHeight, 200,
			preset.Elements, frames, 30, false);

		SKRect Edges(OverlayElementType type)
		{
			OverlayElement element = Get(preset, type) with { Visible = true };
			SKRect drawn = renderer.MeasureElement(element, frames[10])!.Value;
			drawn.Offset(element.X, element.Y);
			return drawn;
		}

		Assert.AreEqual(right, Edges(OverlayElementType.SpeedGauge).Right, 0.5, "SpeedGauge right");
		Assert.AreEqual(bottom, Edges(OverlayElementType.SpeedGauge).Bottom, 0.5, "SpeedGauge bottom");
		Assert.AreEqual(m, Edges(OverlayElementType.MapWidget).Left, 0.5, "MapWidget left");
		Assert.AreEqual(bottom, Edges(OverlayElementType.MapWidget).Bottom, 0.5, "MapWidget bottom");
		Assert.AreEqual(bottom, Edges(OverlayElementType.Compass).Bottom, 0.5, "Compass bottom");
		// The credit sits in the frame's own corner, past the guide.
		Assert.IsTrue(Edges(OverlayElementType.MapAttribution).Left < m, "MapAttribution left of the guide");
		Assert.IsTrue(Edges(OverlayElementType.MapAttribution).Bottom > bottom, "MapAttribution below the guide");
		Assert.IsTrue(Edges(OverlayElementType.MapAttribution).Bottom < OverlayElementBounds.ReferenceHeight, "MapAttribution inside the frame");
		Assert.IsTrue(Edges(OverlayElementType.MapAttribution).Top > Edges(OverlayElementType.MapWidget).Bottom, "MapAttribution under the map");
		Assert.AreEqual(right, Edges(OverlayElementType.GMeter).Right, 0.5, "GMeter right");
		Assert.AreEqual(m, Edges(OverlayElementType.GMeter).Top, 0.5, "GMeter top");
		Assert.AreEqual(right, Edges(OverlayElementType.SunWidget).Right, 0.5, "SunWidget right");
		Assert.AreEqual(bottom, Edges(OverlayElementType.TripProgressBar).Bottom, 0.5, "TripProgressBar bottom");
	}

	/// <summary>Turned on, a widget that's off by default lands in a free spot - not over the map (TripStat) or a readout (Sun).</summary>
	[TestMethod]
	public void CreateDefault_NoWidgetCoversAnother_AllOfThemOn()
	{
		var preset = OverlayPreset.CreateDefault("d", "Default");
		List<DerivedFrame> frames = [];
		for (int i = 0; i < 30; i++)
		{
			frames.Add(new DerivedFrame(new TelemetryFrame(i, i / 30.0, 50, 20, 200, DateTime.UtcNow, 0, 0, 1, Iso: 400, ShutterSeconds: 0.001,
				ColorTemperatureKelvin: 5000), 20, 0, 0, i, 0, 0, new SunPosition(120, 30), 0, 0, 1, 0, 0));
		}

		using var renderer = new OverlayRenderer((int)OverlayElementBounds.ReferenceWidth, (int)OverlayElementBounds.ReferenceHeight, 200,
			preset.Elements, frames, 30, false, "DJI Osmo Action 6");

		List<(OverlayElementType Type, SKRect Drawn)> widgets = [];
		foreach (OverlayElement element in preset.Elements.Select(e => e with { Visible = true }))
		{
			if (renderer.MeasureElement(element, frames[10]) is not { } drawn) continue;
			drawn = new SKRect(drawn.Left * element.Scale, drawn.Top * element.Scale, drawn.Right * element.Scale, drawn.Bottom * element.Scale);
			drawn.Offset(element.X, element.Y);
			widgets.Add((element.Type, drawn));
		}

		for (int i = 0; i < widgets.Count; i++)
		for (int j = i + 1; j < widgets.Count; j++)
			Assert.IsFalse(widgets[i].Drawn.IntersectsWith(widgets[j].Drawn), $"{widgets[i].Type} covers {widgets[j].Type}");
	}

	[TestMethod]
	public void CreateDefault_StaysInsideTheReferenceFrame()
	{
		foreach (OverlayElement element in OverlayPreset.CreateDefault("d", "Default").Elements)
		{
			Assert.IsTrue(element.X is >= 0 and <= OverlayElementBounds.ReferenceWidth, $"{element.Type} X {element.X}");
			Assert.IsTrue(element.Y is >= 0 and <= OverlayElementBounds.ReferenceHeight, $"{element.Type} Y {element.Y}");
		}
	}

	[TestMethod]
	public void ToPixels_ScalesEachAxisOnItsOwn()
	{
		(float x, float y) = OverlayElementBounds.ToPixels(3840, 2160, 2560, 1440);
		Assert.AreEqual(2560f, x, 0.001);
		Assert.AreEqual(1440f, y, 0.001);

		(x, y) = OverlayElementBounds.ToPixels(1920, 1080, 1440, 1080);
		Assert.AreEqual(720f, x, 0.001, "a 4:3 frame's width is its own axis");
		Assert.AreEqual(540f, y, 0.001);
	}

	[TestMethod]
	public void ToReference_UndoesToPixels()
	{
		(float px, float py) = OverlayElementBounds.ToPixels(1234.5f, 678.9f, 1920, 1080);
		(float x, float y) = OverlayElementBounds.ToReference(px, py, 1920, 1080);

		Assert.AreEqual(1234.5f, x, 0.01);
		Assert.AreEqual(678.9f, y, 0.01);
	}
}
