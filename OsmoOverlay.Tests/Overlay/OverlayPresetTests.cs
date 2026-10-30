using System.Globalization;
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

	[TestMethod]
	public void CreateDrone_SwapsTheCameraWidgetsForTheDroneOnes()
	{
		var basis = OverlayPreset.CreateDefault("d", "Default");
		var drone = OverlayPreset.CreateDrone("x", "Drones");

		CollectionAssert.AreEqual(basis.Elements.Select(e => e.Id).ToList(), drone.Elements.Select(e => e.Id).ToList());
		Dictionary<string, OverlayElement> slots = drone.Elements.ToDictionary(e => e.Id);
		foreach (OverlayElement element in drone.Elements)
		{
			OverlayElement original = basis.Elements.Single(e => e.Id == element.Id);
			bool expected = element switch
			{
				DateTimeTextElement or ElevationElement or GradientElement or RollGaugeElement or GMeterElement or CompassElement or SunWidgetElement or GpsAlertElement or VerticalSpeedElement or FlightModeElement
					or HomeDistanceElement or HeightElement or TripProgressBarElement => false,
				ProfileChartElement or FlightAlertsElement or SatellitesElement or SignalElement or DroneBatteryElement or TripStatElement
					or RcSticksElement or AttitudeIndicatorElement or Orientation3DElement => true,
				_ => original.Visible
			};
			Assert.AreEqual(expected, element.Visible, element.Type.ToString());
			if (element is not (SatellitesElement or SignalElement or HeightElement or DistanceElement or DroneBatteryElement or TripStatElement
		    or RcSticksElement or AttitudeIndicatorElement or Orientation3DElement or ProfileChartElement))
				Assert.IsTrue(original.X == element.X && original.Y == element.Y, element.Type.ToString());
		}

		// Signal, GPS and the battery in a row in the top right, the left column down from the top.
		Assert.AreEqual(slots[nameof(OverlayElementType.Signal)].Y, slots[nameof(OverlayElementType.Satellites)].Y);
		Assert.AreEqual(slots[nameof(OverlayElementType.Signal)].Y, slots[nameof(OverlayElementType.DroneBattery)].Y);
		Assert.IsTrue(slots[nameof(OverlayElementType.Satellites)].X > slots[nameof(OverlayElementType.Signal)].X);
		Assert.IsTrue(slots[nameof(OverlayElementType.DroneBattery)].X < slots[nameof(OverlayElementType.Signal)].X, "the battery is before the signal");
		Assert.IsTrue(slots[nameof(OverlayElementType.Signal)].X > OverlayElementBounds.ReferenceWidth / 2);
		Assert.IsTrue(slots[nameof(OverlayElementType.DroneBattery)].Y < slots[nameof(OverlayElementType.Distance)].Y, "the column starts under the battery");
	}

	[TestMethod]
	public void CreateFpv_IsAnOsdOfItsOwnWithSnappyAlerts()
	{
		var drone = OverlayPreset.CreateDrone("x", "Drones");
		var fpv = OverlayPreset.CreateFpv("y", "FPV");

		Assert.IsTrue(drone.Elements.Select(e => e.Id).All(id => fpv.Elements.Any(e => e.Id == id)));
		Assert.AreEqual(fpv.Elements.Count, fpv.Elements.Select(e => e.Id).Distinct().Count());
		foreach (OverlayElementType type in new[] { OverlayElementType.FlightMode, OverlayElementType.ElapsedTimeText, OverlayElementType.VerticalSpeed, OverlayElementType.RcSticks, OverlayElementType.AttitudeIndicator, OverlayElementType.Orientation3D, OverlayElementType.DateTimeText })
			Assert.IsTrue(fpv.Elements.Single(e => e.Type == type).Visible, type.ToString());
		Assert.IsTrue(fpv.Elements.Single(e => e is MapWidgetElement).Visible && fpv.Elements.Single(e => e is MapAttributionElement).Visible, "the map and its credit go together");
		foreach (OverlayElementType type in new[] { OverlayElementType.ProfileChart, OverlayElementType.TripProgressBar })
			Assert.IsFalse(fpv.Elements.Single(e => e.Type == type).Visible, type.ToString());
		Assert.IsFalse(drone.Elements.Single(e => e is FlightModeElement).Visible);
		var calm = (FlightAlertsElement)drone.Elements.Single(e => e is FlightAlertsElement);
		var snappy = (FlightAlertsElement)fpv.Elements.Single(e => e is FlightAlertsElement);
		Assert.IsTrue(snappy.FadeSeconds < calm.FadeSeconds && snappy.ShowSeconds < calm.ShowSeconds && snappy.SlideIn && !calm.SlideIn);
		Assert.IsTrue(fpv.Elements.OfType<LabeledStatElement>().All(e => e.AccentColor == "#FFFFFF"), "the OSD is white");
		AssertWidgetsDoNotOverlap(fpv, allOn: false);
	}

		[TestMethod]
	public void CreateDrone_NoShownWidgetCoversAnother()
	{
		AssertWidgetsDoNotOverlap(OverlayPreset.CreateDrone("x", "Drones"), allOn: false);
	}

	[TestMethod]
	public void TheProfileComesInAfterTheWatermark_AndOnTheBottomGuideInTheDronePreset()
	{
		var basis = OverlayPreset.CreateDefault("d", "Default");
		var drone = OverlayPreset.CreateDrone("x", "Drones");
		double credit = basis.Elements.OfType<WatermarkElement>().Single().DisappearAtSeconds!.Value;
		foreach (OverlayPreset preset in new[] { basis, drone })
		{
			OverlayElement chart = Get(preset, OverlayElementType.ProfileChart);
			Assert.AreEqual(credit, chart.AppearAtSeconds);
			Assert.AreEqual(nameof(OverlayElementType.Watermark), chart.LayerId);
		}

		List<DerivedFrame> frames = [];
		for (int i = 0; i < 30; i++)
			frames.Add(new DerivedFrame(new TelemetryFrame(i, i / 30.0, 50, 20, 200, null, 0, 0, 1), 20, 0, 0, i, 0, 0, new SunPosition(120, 30), 0, 0, 1, 0, 0));
		using var renderer = new OverlayRenderer((int)OverlayElementBounds.ReferenceWidth, (int)OverlayElementBounds.ReferenceHeight, 200, drone.Elements, frames, 30, false);
		OverlayElement droneChart = Get(drone, OverlayElementType.ProfileChart);
		SKRect drawn = renderer.MeasureElement(droneChart, frames[10])!.Value;
		drawn.Offset(droneChart.X, droneChart.Y);
		Assert.AreEqual(OverlayElementBounds.ReferenceHeight - OverlayElementBounds.Margin, drawn.Bottom, 0.5, "ProfileChart bottom");
	}

	[TestMethod]
	public void CreateFpv_LinesTheRightColumnUpToTheRightMarginAndCentersTheMode()
	{
		var fpv = OverlayPreset.CreateFpv("y", "FPV");
		const float edge = OverlayElementBounds.ReferenceWidth - OverlayElementBounds.Margin;

		foreach (OverlayElementType type in new[] { OverlayElementType.Height, OverlayElementType.HomeDistance, OverlayElementType.VerticalSpeed, OverlayElementType.ElapsedTimeText, OverlayElementType.DateTimeText })
		{
			var element = (StyledOverlayElement)Get(fpv, type);
			Assert.AreEqual(TextAlign.Right, element.TextAlign, type.ToString());
			Assert.AreEqual(edge, element.X + OverlayElementBounds.TextBoxRight(element), 0.5, $"{type} ends on the margin");
		}

		var mode = (StyledOverlayElement)Get(fpv, OverlayElementType.FlightMode);
		Assert.AreEqual(TextAlign.Center, mode.TextAlign);
		Assert.AreEqual(OverlayElementBounds.ReferenceWidth / 2, mode.X + OverlayElementBounds.TextBoxRight(mode) / 2, 0.5);
	}

	[TestMethod]
	public void IsBuiltIn_IsDefaultAndTheDronePreset()
	{
		Assert.IsTrue(OverlayPresetStore.IsBuiltIn(OverlayPresetStore.DefaultPresetId));
		Assert.IsTrue(OverlayPresetStore.IsBuiltIn(OverlayPresetStore.DronePresetId));
		Assert.IsTrue(OverlayPresetStore.IsBuiltIn(OverlayPresetStore.FpvPresetId));
		Assert.IsTrue(OverlayPresetStore.IsDronePreset(OverlayPresetStore.FpvPresetId));
		Assert.IsFalse(OverlayPresetStore.IsDronePreset(OverlayPresetStore.DefaultPresetId));
		Assert.IsFalse(OverlayPresetStore.IsBuiltIn(Guid.NewGuid().ToString("N")));
		Assert.IsFalse(OverlayPresetStore.IsBuiltIn(null));
	}

	/// <summary>Turned on, a widget that's off by default lands in a free spot - not over the map (TripStat) or a readout (Sun).</summary>
	[TestMethod]
	[DataRow("en")]
	[DataRow("pl")]
	public void CreateDefault_NoWidgetCoversAnother_AllOfThemOn(string language)
	{
		CultureInfo previous = CultureInfo.CurrentUICulture;
		try
		{
			CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
			AssertDefaultWidgetsDoNotOverlap();
		}
		finally
		{
			CultureInfo.CurrentUICulture = previous;
		}
	}

	private static void AssertDefaultWidgetsDoNotOverlap()
	{
		AssertWidgetsDoNotOverlap(OverlayPreset.CreateDefault("d", "Default"), allOn: true);
	}

	private static void AssertWidgetsDoNotOverlap(OverlayPreset preset, bool allOn)
	{
		List<DerivedFrame> frames = [];
		for (int i = 0; i < 30; i++)
		{
			frames.Add(new DerivedFrame(new TelemetryFrame(i, i / 30.0, 50, 20, 200, DateTime.UtcNow, 0, 0, 1, Iso: 400, ShutterSeconds: 0.001,
				ColorTemperatureKelvin: 5000, AmbientTemperatureCelsius: -23.4, ImuTemperatureCelsius: 29.8,
				DeviceDistanceMeters: 12345, DeviceBatteryPercent: 100, AudioRmsDbfs: -128, AudioPeakDbfs: -128,
				WindProcessingActive: false, WindProcessingLevel: 100, ImageLuma: 255), 20, 0, 0, i, 0, 0, new SunPosition(120, 30), 0, 0, 1, 0, 0));
		}

		using var renderer = new OverlayRenderer((int)OverlayElementBounds.ReferenceWidth, (int)OverlayElementBounds.ReferenceHeight, 200,
			preset.Elements, frames, 30, false, "DJI Osmo Action 6");

		List<(OverlayElementType Type, SKRect Drawn, double From, double Until)> widgets = [];
		foreach (OverlayElement element in preset.Elements.Select(e => allOn ? e with { Visible = true } : e).Where(e => e.Visible))
		{
			if (renderer.MeasureElement(element, frames[10]) is not { } drawn) continue;
			drawn = new SKRect(drawn.Left * element.Scale, drawn.Top * element.Scale, drawn.Right * element.Scale, drawn.Bottom * element.Scale);
			drawn.Offset(element.X, element.Y);
			widgets.Add((element.Type, drawn, element.AppearAtSeconds ?? 0, element.DisappearAtSeconds ?? double.PositiveInfinity));
		}

		// Two taking turns in one place (the watermark, then the progress bar) never cover each other.
		for (int i = 0; i < widgets.Count; i++)
		for (int j = i + 1; j < widgets.Count; j++)
		{
			bool together = widgets[i].From < widgets[j].Until && widgets[j].From < widgets[i].Until;
			Assert.IsFalse(together && widgets[i].Drawn.IntersectsWith(widgets[j].Drawn), $"{widgets[i].Type} covers {widgets[j].Type}");
		}
	}

	[TestMethod]
	public void CreateDefault_GpsAlert_IsOn_AndCoversNoWidgetThatIsOnByDefault()
	{
		// The alert is on screen only with a GPS notice, so the test above can't measure it - its panel is measured here.
		var preset = OverlayPreset.CreateDefault("d", "Default");
		GpsAlertElement alert = preset.Elements.OfType<GpsAlertElement>().Single();
		Assert.IsTrue(alert.Visible);

		List<DerivedFrame> frames = [];
		for (int i = 0; i < 30; i++)
		{
			frames.Add(new DerivedFrame(new TelemetryFrame(i, i / 30.0, 50, 20, 200, DateTime.UtcNow, 0, 0, 1, Iso: 400, ShutterSeconds: 0.001,
				ColorTemperatureKelvin: 5000), 20, 0, 0, i, 0, 0, new SunPosition(120, 30), 0, 0, 1, 0, 0));
		}

		using var renderer = new OverlayRenderer((int)OverlayElementBounds.ReferenceWidth, (int)OverlayElementBounds.ReferenceHeight, 200,
			preset.Elements, frames, 30, false, "DJI Osmo Action 6");
		SKRect panel = OverlayElementBounds.GpsAlertBox(alert.FontFamily, alert.Texts);
		panel.Offset(alert.X, alert.Y);

		foreach (OverlayElement element in preset.Elements.Where(e => e.Visible && e is not GpsAlertElement))
		{
			if (renderer.MeasureElement(element, frames[10]) is not { } drawn) continue;
			drawn.Offset(element.X, element.Y);
			Assert.IsFalse(panel.IntersectsWith(drawn), $"the GPS alert covers {element.Type}");
		}
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
