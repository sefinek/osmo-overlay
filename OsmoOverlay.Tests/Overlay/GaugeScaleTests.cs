using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class GaugeScaleTests
{
	[TestMethod]
	[DataRow(0.0, 20.0)]
	[DataRow(14.0, 20.0)]
	[DataRow(20.0, 20.0)]
	[DataRow(20.1, 30.0)]
	[DataRow(37.0, 40.0)]
	[DataRow(1000.0, 500.0)]
	public void SnapGaugeMaxSpeed_RoundsUpToTheStepWithinTheRange(double speed, double expected)
	{
		Assert.AreEqual(expected, OverlayRenderer.SnapGaugeMaxSpeed(speed));
	}

	[TestMethod]
	public void ConvertGaugeMaxSpeed_SnapsToTheNewUnitsGrid()
	{
		Assert.AreEqual(80.0, OverlayRenderer.ConvertGaugeMaxSpeed(120, UnitSystem.Metric, UnitSystem.Imperial));
		Assert.AreEqual(130.0, OverlayRenderer.ConvertGaugeMaxSpeed(80, UnitSystem.Imperial, UnitSystem.Metric));
		Assert.AreEqual(120.0, OverlayRenderer.ConvertGaugeMaxSpeed(120, UnitSystem.Metric, UnitSystem.Metric));
	}
}
