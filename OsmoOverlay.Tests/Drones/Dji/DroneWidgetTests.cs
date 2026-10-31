using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Drones.Dji;

[TestClass]
public sealed class DroneWidgetTests
{
	private static readonly OverlayElementType[] Types =
	[
		OverlayElementType.Height, OverlayElementType.HomeDistance, OverlayElementType.VerticalSpeed, OverlayElementType.Satellites
	];

	[TestMethod]
	public void EachWidgetNeedsItsOwnReadingAndNoCameraAxes()
	{
		TelemetryFrame frame = new(0, 0, 50, 20, 100, null, 0, 0, 1, HasGpsFix: false);
		var none = OverlayAvailability.Of([frame], false, null);
		foreach (OverlayElementType type in Types) Assert.IsFalse(OverlayDataRequirements.IsSupported(type, none), type.ToString());

		Assert.IsTrue(OverlayDataRequirements.IsSupported(OverlayElementType.VerticalSpeed, OverlayAvailability.Of([frame with { VerticalSpeedMs = 0 }], false, null)));
		Assert.IsTrue(OverlayDataRequirements.IsSupported(OverlayElementType.Satellites, OverlayAvailability.Of([frame with { GpsSatellites = 0 }], false, null)));
		Assert.IsFalse(OverlayDataRequirements.IsSupported(OverlayElementType.Satellites, OverlayAvailability.Of([frame with { VerticalSpeedMs = 1 }], false, null)));
	}

	[TestMethod]
	public void TheDronePresetShowsTheGpsAndTheSignalButNotTheClimbRateOrTheGpsAlert()
	{
		OverlayPreset drone = OverlayPreset.CreateDrone("x", "Drones");

		Assert.IsFalse(drone.Elements.Single(e => e is VerticalSpeedElement).Visible);
		Assert.IsFalse(drone.Elements.Single(e => e is GpsAlertElement).Visible);
		Assert.IsTrue(drone.Elements.OfType<LabeledStatElement>().All(e => e.AccentColor is null), "the colors are the default ones");
		Assert.IsTrue(drone.Elements.Single(e => e is SatellitesElement).Visible);
		Assert.IsTrue(drone.Elements.Single(e => e is SignalElement).Visible);
	}

	[TestMethod]
	public void TheSignalWidgetNeedsALinkReading()
	{
		TelemetryFrame frame = new(0, 0, 50, 20, 100, null, 0, 0, 1);

		Assert.IsFalse(OverlayAvailability.Of([frame], false, null).Signal);
		Assert.IsFalse(OverlayAvailability.Of([frame with { DroneSignal = new DroneSignal() }], false, null).Signal);
		Assert.IsTrue(OverlayAvailability.Of([frame with { DroneSignal = new DroneSignal(null, 0) }], false, null).Signal);
	}

	[TestMethod]
	[DataRow(0.0, 0)]
	[DataRow(9.0, 0)]
	[DataRow(10.0, 1)]
	[DataRow(55.0, 3)]
	[DataRow(70.0, 4)]
	[DataRow(100.0, 5)]
	[DataRow(140.0, 5)]
	public void BarsAreOneInFivePercentSteps(double percent, int bars)
	{
		Assert.AreEqual(bars, OverlayRenderer.BarsOf(percent));
	}

	[TestMethod]
	public void TheSignalWidgetIsAsWideAsItsLinesAndDropsTheBarsAndLinesSwitchedOff()
	{
		TelemetryFrame raw = new(0, 0, 50, 20, 100, null, 0, 0, 1, DroneSignal: new DroneSignal(92, 88), GpsLevel: 5, GpsSatellites: 23);
		List<DerivedFrame> frames = TelemetryProcessor.Process([raw], null);
		using var renderer = new OverlayRenderer(1280, 720, 0, [], frames, showWatermark: false);
		var signal = new SignalElement { X = 100, Y = 100 };

		float full = renderer.MeasureElement(signal, frames[0])!.Value.Width;
		Assert.IsTrue(renderer.MeasureElement(signal with { ShowBars = false }, frames[0])!.Value.Width < full);
		float both = renderer.MeasureElement(signal, frames[0])!.Value.Height;
		Assert.AreEqual(both, renderer.MeasureElement(signal with { ShowDownlink = false }, frames[0])!.Value.Height, 1, "the last line stays on the GPS count's baseline, so one line leaves the widget as tall");
		Assert.IsTrue(renderer.MeasureElement(new SatellitesElement { X = 100, Y = 100 }, frames[0])!.Value.Width
		              > renderer.MeasureElement(new SatellitesElement { X = 100, Y = 100, ShowBars = false }, frames[0])!.Value.Width);
	}

	[TestMethod]
	public void TheDronePresetHasNoIntroAnimationAndNoSunOrGMeterAndKeepsTheAlertsShort()
	{
		OverlayPreset drone = OverlayPreset.CreateDrone("x", "Drones");

		Assert.IsTrue(drone.Elements.All(e => e.AppearAtSeconds == OverlayPreset.CreateDefault("d", "D").Elements.Single(o => o.Id == e.Id).AppearAtSeconds));
		Assert.IsTrue(drone.Elements.All(e => e.AnimationType == OverlayPreset.CreateDefault("d", "D").Elements.Single(o => o.Id == e.Id).AnimationType));
		Assert.IsFalse(drone.Elements.Single(e => e is SunWidgetElement).Visible);
		Assert.IsFalse(drone.Elements.Single(e => e is GMeterElement).Visible);

		var alerts = (FlightAlertsElement)OverlayPreset.CreateFpv("x", "D").Elements.Single(e => e is FlightAlertsElement);
		Assert.IsTrue(alerts.FadeSeconds < new FlightAlertsElement { X = 0, Y = 0 }.FadeSeconds);
		Assert.IsTrue(alerts.ShowSeconds < new FlightAlertsElement { X = 0, Y = 0 }.ShowSeconds);
		Assert.IsTrue(alerts.SlideIn);
	}

	[TestMethod]
	public void TheFlightModeWidgetShowsTheModeOfTheFrameAndNeedsOne()
	{
		TelemetryFrame raw = new(0, 0, 50, 20, 100, null, 0, 0, 1, FlightMode: "Sport");
		List<DerivedFrame> frames = TelemetryProcessor.Process([raw, raw with { FrameNumber = 1, SampleTimeSeconds = 1, FlightMode = null }], null);
		using var renderer = new OverlayRenderer(1280, 720, 0, [], frames, showWatermark: false);
		var element = new FlightModeElement { X = 100, Y = 100 };

		Assert.IsTrue(OverlayAvailability.Of([raw], false, null).FlightMode);
		Assert.IsFalse(OverlayAvailability.Of([raw with { FlightMode = null }], false, null).FlightMode);
		Assert.IsNotNull(renderer.MeasureElement(element, frames[0]));
		Assert.IsFalse(OverlayPreset.CreateDrone("x", "D").Elements.Single(e => e is FlightModeElement).Visible, "the DJI drone preset leaves the mode to the FPV one");
		Assert.IsTrue(OverlayPreset.CreateFpv("x", "D").Elements.Single(e => e is FlightModeElement).Visible);
		Assert.IsFalse(OverlayPreset.CreateDefault("d", "D").Elements.Single(e => e is FlightModeElement).Visible, "the camera preset has it off");
	}

	[TestMethod]
	public void TheDroneFormatIsADroneAndTheOthersAreNot()
	{
		Assert.IsTrue(((OsmoOverlay.Core.Cameras.ICameraFormat)new OsmoOverlay.Drones.Dji.DjiDroneFormat()).IsDrone);
		Assert.IsFalse(((OsmoOverlay.Core.Cameras.ICameraFormat)new OsmoOverlay.Cameras.Dji.DjiOsmoFormat()).IsDrone);
	}

	[TestMethod]
	public void NewWidgetsRoundTripThroughJson()
	{
		var preset = new OverlayPreset("p", "drone",
		[
			new VerticalSpeedElement { Id = "v", X = 100, Y = 200, Units = UnitSystem.Imperial, Label = "Climb" },
			new SatellitesElement { Id = "s", X = 300, Y = 200 }
		]);

		OverlayPreset loaded = OverlayPresetStore.Sanitize(System.Text.Json.JsonSerializer.Deserialize<OverlayPreset>(System.Text.Json.JsonSerializer.Serialize(preset)));

		CollectionAssert.AreEqual(preset.Elements, loaded.Elements);
	}

	[TestMethod]
	public void TheClimbRateIsSignedInTheChosenUnit()
	{
		TelemetryFrame raw = new(0, 0, 50, 20, 100, null, 0, 0, 1, VerticalSpeedMs: -2);

		Assert.AreEqual(("-2.0", "M/S"), Draw(raw, UnitSystem.Metric));
		Assert.AreEqual(("-6.6", "FT/S"), Draw(raw, UnitSystem.Imperial));
		Assert.AreEqual(("+1.5", "M/S"), Draw(raw with { VerticalSpeedMs = 1.5 }, UnitSystem.Metric));
		Assert.AreEqual(("0.0", "M/S"), Draw(raw with { VerticalSpeedMs = -0.02 }, UnitSystem.Metric), "no minus sign on a zero");
		Assert.AreEqual(("--", "M/S"), Draw(raw with { VerticalSpeedMs = null }, UnitSystem.Metric));

		static (string, string) Draw(TelemetryFrame frame, UnitSystem units) => OverlayRenderer.FormatVerticalSpeed(frame.VerticalSpeedMs, units);
	}
}
