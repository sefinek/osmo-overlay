using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public class DroneMarkerTests
{
	private static DerivedFrame Frame(double travel, DroneAttitude? attitude)
	{
		TelemetryFrame raw = new(0, 0, 50, 20, 100, null, 0, 0, 1, DroneAttitude: attitude);
		return new DerivedFrame(raw, 20, travel, 0, 0, 0, 0, default, 0, 0, 0, 0, 0, false, StableHeadingDegrees: travel);
	}

	[TestMethod]
	[DataRow(0.0, 350.0, false)]
	[DataRow(90.0, 0.0, true)]
	[DataRow(180.0, 0.0, true)]
	[DataRow(10.0, 355.0, false)]
	public void DotOnlyWhenNoseIsFarFromTravel(double yaw, double travel, bool dot)
	{
		var element = new MapWidgetElement { X = 0, Y = 0, DotWhenNotFacingTravel = true };
		Assert.AreEqual(dot, OverlayRenderer.DroneNotFacingTravel(Frame(travel, new DroneAttitude(0, 0, yaw)), element));
	}

	[TestMethod]
	public void DronePresetsTurnTheDotOn_OthersDoNot()
	{
		static bool On(OverlayPreset preset) => preset.Elements.OfType<MapWidgetElement>().Single().DotWhenNotFacingTravel;

		Assert.IsTrue(On(OverlayPreset.CreateDrone("d", "d")));
		Assert.IsTrue(On(OverlayPreset.CreateFpv("f", "f")));
		Assert.IsFalse(On(OverlayPreset.CreateDefault("x", "x")));
	}

	[TestMethod]
	public void NoDotWithoutSettingOrAttitude()
	{
		Assert.IsFalse(OverlayRenderer.DroneNotFacingTravel(Frame(0, new DroneAttitude(0, 0, 180)), new MapWidgetElement { X = 0, Y = 0 }));
		Assert.IsFalse(OverlayRenderer.DroneNotFacingTravel(Frame(0, null), new MapWidgetElement { X = 0, Y = 0, DotWhenNotFacingTravel = true }));
	}
}
