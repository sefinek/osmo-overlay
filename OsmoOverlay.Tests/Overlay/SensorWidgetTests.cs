using System.Globalization;
using System.Text.Json;
using OsmoOverlay.Cameras.Dji;
using OsmoOverlay.Cameras.Insta360;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class SensorWidgetTests
{
	private static readonly OverlayElementType[] Types =
	[
		OverlayElementType.AmbientTemperature, OverlayElementType.ImuTemperature,
		OverlayElementType.DeviceDistance, OverlayElementType.DeviceBattery,
		OverlayElementType.AudioLevel, OverlayElementType.WindProcessing, OverlayElementType.ImageLuma
	];

	[TestMethod]
	public void DjiAndInstaWithoutSensorReadingsDisableTheNewWidgets()
	{
		List<TelemetryFrame> frames = [new(0, 0, 50, 20, 100, DateTime.UtcNow, 0, 0, 1)];
		foreach (ICameraFormat camera in new ICameraFormat[] { new DjiOsmoFormat(), new Insta360Format() })
		{
			var availability = OverlayAvailability.Of(frames, true, camera);
			foreach (OverlayElementType type in Types) Assert.IsFalse(OverlayDataRequirements.IsSupported(type, availability), $"{camera.Id}: {type}");
			OverlayElement[] layout = [.. OverlayPreset.CreateDefault("p", "test").Elements.Where(e => Types.Contains(e.Type)).Select(e => e with { Visible = true })];
			Assert.IsTrue(availability.Apply(layout).All(e => !e.Visible));
			Assert.IsTrue(layout.All(e => e.Visible), "the preset is unchanged");
		}
	}

	[TestMethod]
	public void EachWidgetRequiresItsOwnReading()
	{
		TelemetryFrame frame = new(0, 0, 50, 20, 100, null, 0, 0, 1, HasGpsFix: false, ImuTemperatureCelsius: 0);
		var availability = OverlayAvailability.Of([frame], false, null);
		Assert.IsTrue(OverlayDataRequirements.IsSupported(OverlayElementType.ImuTemperature, availability), "zero is a real reading, and GPS is not needed");
		foreach (OverlayElementType type in Types.Where(t => t != OverlayElementType.ImuTemperature)) Assert.IsFalse(OverlayDataRequirements.IsSupported(type, availability));
		availability = OverlayAvailability.Of([
			frame with
			{
				AmbientTemperatureCelsius = -5, DeviceDistanceMeters = 0, DeviceBatteryPercent = 0,
				AudioRmsDbfs = -128, AudioPeakDbfs = 0, WindProcessingActive = false, WindProcessingLevel = 0, ImageLuma = 0
			}
		], false, null);
		foreach (OverlayElementType type in Types) Assert.IsTrue(OverlayDataRequirements.IsSupported(type, availability));
	}

	[TestMethod]
	public void NewWidgetTypesRoundTripWithLabelsAndUnits()
	{
		var preset = new OverlayPreset("p", "sensors",
		[
			new AmbientTemperatureElement { Id = "a", X = 100, Y = 200, Units = UnitSystem.Imperial, Label = "Weather" },
			new ImuTemperatureElement { Id = "i", X = 200, Y = 200 },
			new DeviceDistanceElement { Id = "d", X = 100, Y = 400, Units = UnitSystem.Imperial },
			new DeviceBatteryElement { Id = "b", X = 200, Y = 400 },
			new AudioLevelElement { Id = "audio", X = 100, Y = 600, Label = "Mic", AccentColor = "#123456" },
			new WindProcessingElement { Id = "wind", X = 200, Y = 600 },
			new ImageLumaElement { Id = "luma", X = 100, Y = 800 }
		]);
		OverlayPreset loaded = OverlayPresetStore.Sanitize(JsonSerializer.Deserialize<OverlayPreset>(JsonSerializer.Serialize(preset)));
		CollectionAssert.AreEqual(preset.Elements, loaded.Elements);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void AudioBarRendersAccentFillAndCurrentPeakIndependentlyOfPlaybackOrder(bool shadows)
	{
		TelemetryFrame raw = new(0, 0, 0, 0, 0, null, 0, 0, 1, HasGpsFix: false, AudioRmsDbfs: -60, AudioPeakDbfs: -50);
		List<DerivedFrame> frames = TelemetryProcessor.Process([raw, raw with { FrameNumber = 1, SampleTimeSeconds = 1, AudioRmsDbfs = -30, AudioPeakDbfs = -12 }], null);
		var element = new AudioLevelElement { X = 300, Y = 300, AccentColor = "#FF0000", TextColor = "#00FF00", ShadowEnabled = shadows };
		using var renderer = new OverlayRenderer(1280, 720, 0, [element], frames, showWatermark: false);
		byte[] first = new byte[renderer.FrameBufferSize()];
		byte[] second = new byte[first.Length];
		renderer.RenderInto(frames[1], first, premultiplied: true);
		int rmsPixel = (112 * 1280 + 150) * 4;
		Assert.AreEqual((byte)255, first[rmsPixel + 2], "RMS fill uses the widget's accent color");
		Assert.AreEqual((byte)0, first[rmsPixel + 1]);
		int peakPixel = (112 * 1280 + 265) * 4;
		Assert.AreEqual((byte)255, first[peakPixel + 1], "peak marker uses the text color");
		renderer.RenderInto(frames[0], second, premultiplied: true);
		renderer.RenderInto(frames[1], second, premultiplied: true);
		CollectionAssert.AreEqual(first, second);
	}

	[TestMethod]
	public void CameraDiagnosticsNeedTheirOwnValidValuesAndNoGps()
	{
		TelemetryFrame frame = new(0, 0, 0, 0, 0, null, 0, 0, 1, HasGpsFix: false,
			AudioRmsDbfs: double.NaN, AudioPeakDbfs: 1, WindProcessingLevel: 101, ImageLuma: -1);
		var invalid = OverlayAvailability.Of([frame], false, null);
		Assert.IsFalse(invalid.AudioLevel);
		Assert.IsFalse(invalid.WindProcessing);
		Assert.IsFalse(invalid.ImageLuma);
		var valid = OverlayAvailability.Of([frame with { AudioPeakDbfs = -10, WindProcessingActive = false, ImageLuma = 255 }], false, null);
		Assert.IsTrue(valid.AudioLevel);
		Assert.IsTrue(valid.WindProcessing);
		Assert.IsTrue(valid.ImageLuma);
	}

	[TestMethod]
	[DataRow(-128.0, 0.0)]
	[DataRow(-60.0, 0.0)]
	[DataRow(-30.0, 0.5)]
	[DataRow(0.0, 1.0)]
	public void AudioBarUsesDbfsWithoutTreatingItAsLinearAmplitude(double dbfs, double expected)
	{
		Assert.AreEqual(expected, OverlayRenderer.AudioMeterFraction(dbfs), 1e-6);
	}

	[TestMethod]
	[DataRow(0.0, 32.0)]
	[DataRow(-40.0, -40.0)]
	[DataRow(100.0, 212.0)]
	public void TemperatureUsesCelsiusOrFahrenheit(double celsius, double fahrenheit)
	{
		Assert.AreEqual((celsius.ToString("0.0", CultureInfo.InvariantCulture), "°C"), OverlayRenderer.FormatTemperature(celsius, UnitSystem.Metric));
		Assert.AreEqual((fahrenheit.ToString("0.0", CultureInfo.InvariantCulture), "°F"), OverlayRenderer.FormatTemperature(celsius, UnitSystem.Imperial));
		Assert.AreEqual(("--", "°C"), OverlayRenderer.FormatTemperature(null, UnitSystem.Metric));
	}
}
