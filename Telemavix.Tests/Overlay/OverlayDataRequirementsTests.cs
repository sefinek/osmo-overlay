using Telemavix.Cameras.Insta360;
using Telemavix.Core.Overlay;
using Telemavix.Core.Telemetry;

namespace Telemavix.Tests.Overlay;

[TestClass]
public sealed class OverlayDataRequirementsTests
{
	[TestMethod]
	// Accelerometer-only widgets work with no GPS at all.
	[DataRow(OverlayElementType.RollGauge, false, false, false, true)]
	[DataRow(OverlayElementType.PitchGauge, false, false, false, true)]
	[DataRow(OverlayElementType.GMeter, false, false, false, true)]
	// No telemetry needed at all.
	[DataRow(OverlayElementType.Text, false, false, false, true)]
	[DataRow(OverlayElementType.Image, false, false, false, true)]
	// Position-based widgets need a fix.
	[DataRow(OverlayElementType.SpeedGauge, false, true, true, false)]
	[DataRow(OverlayElementType.MapWidget, false, true, true, false)]
	[DataRow(OverlayElementType.ProfileChart, false, true, true, false)]
	[DataRow(OverlayElementType.TripStat, false, true, true, false)]
	[DataRow(OverlayElementType.SpeedGauge, true, false, false, true)]
	// Time falls back to the container's creation_time.
	[DataRow(OverlayElementType.DateTimeText, false, false, true, true)]
	[DataRow(OverlayElementType.UtcTimeText, false, false, false, false)]
	// The sun needs a real position and a real GPS clock - not the camera's own clock.
	[DataRow(OverlayElementType.SunWidget, true, false, true, false)]
	[DataRow(OverlayElementType.SunWidget, true, true, false, true)]
	public void IsSupported(OverlayElementType type, bool gpsFix, bool gpsTimestamp, bool containerTime, bool expected)
	{
		Assert.AreEqual(expected, OverlayDataRequirements.IsSupported(type, new OverlayAvailability(gpsFix, gpsTimestamp, containerTime)));
	}

	[TestMethod]
	[DataRow(OverlayElementType.Elevation, false)]
	[DataRow(OverlayElementType.Gradient, false)]
	[DataRow(OverlayElementType.SpeedGauge, true)]
	[DataRow(OverlayElementType.MapWidget, true)]
	public void IsSupported_AFlightLogHasNoAltitudeAboveSea(OverlayElementType type, bool expected)
	{
		Assert.AreEqual(expected, OverlayDataRequirements.IsSupported(type, new OverlayAvailability(true, true, true, AltitudeAboveSea: false)));
		Assert.IsTrue(OverlayDataRequirements.IsSupported(OverlayElementType.Elevation, new OverlayAvailability(true, true, true)));
	}

	[TestMethod]
	public void Of_AltitudeAboveSeaIsLostOnlyWithHeightAboveTakeOff()
	{
		List<TelemetryFrame> plain = [new(0, 0, 0, 0, 0, null, 0, 0, 1, ShutterSeconds: 0.001, HasGpsFix: true)];
		List<TelemetryFrame> drone = [plain[0] with { HeightAboveHomeMeters = 12 }];

		Assert.IsTrue(OverlayAvailability.Of(plain, true, null).AltitudeAboveSea);
		Assert.IsFalse(OverlayAvailability.Of(drone, true, null).AltitudeAboveSea);
	}

	[TestMethod]
	[DataRow(OverlayElementType.RollGauge, true)]
	[DataRow(OverlayElementType.PitchGauge, true)]
	[DataRow(OverlayElementType.GMeter, true)]
	[DataRow(OverlayElementType.CameraInfo, false)]
	[DataRow(OverlayElementType.ElapsedTimeText, true)]
	[DataRow(OverlayElementType.CameraModelText, true)]
	[DataRow(OverlayElementType.DateTimeText, true)]
	public void Insta360_TiltFromItsAxes_NoCameraSettings(OverlayElementType type, bool expected)
	{
		List<TelemetryFrame> frames = [new(0, 0, 0, 0, 0, null, 0, 0, 1, ShutterSeconds: 0.001, HasGpsFix: false)];

		Assert.AreEqual(expected, OverlayDataRequirements.IsSupported(type, OverlayAvailability.Of(frames, true, new Insta360Format())));
	}

	[TestMethod]
	public void ApplyAvailability_HidesOnlyUnsupported_WithoutTouchingThePreset()
	{
		List<OverlayElement> preset =
		[
			new SpeedGaugeElement { X = 0, Y = 0, Visible = true },
			new RollGaugeElement { X = 0, Y = 0, Visible = true },
			new MapWidgetElement { X = 0, Y = 0, Visible = false }
		];

		IReadOnlyList<OverlayElement> applied = new OverlayAvailability(false, false, true).Apply(preset);

		CollectionAssert.AreEqual(new[] { false, true, false }, applied.Select(e => e.Visible).ToArray());
		Assert.IsTrue(preset[0].Visible, "the saved preset itself must stay as the user arranged it");
		Assert.AreSame(preset[1], applied[1], "supported elements pass through untouched");
	}
}
