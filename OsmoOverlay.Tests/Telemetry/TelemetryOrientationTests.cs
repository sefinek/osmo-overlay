using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Telemetry;

[TestClass]
public sealed class TelemetryOrientationTests
{
	[TestMethod]
	public void TryCreate_NormalizesRoundingDrift()
	{
		TelemetryOrientation q = TelemetryOrientation.TryCreate(1.000001, 0, 0, 0)!.Value;

		Assert.AreEqual(1f, q.W);
	}

	[TestMethod]
	[DataRow(0, 0, 0, 0)]
	[DataRow(double.NaN, 0, 0, 1)]
	[DataRow(double.PositiveInfinity, 0, 0, 0)]
	[DataRow(2, 0, 0, 0)]
	[DataRow(0.5, 0, 0, 0)]
	public void TryCreate_RejectsWhatIsNotARotation(double w, double x, double y, double z)
	{
		Assert.IsNull(TelemetryOrientation.TryCreate(w, x, y, z));
	}

	[TestMethod]
	public void AngleDegreesTo_QAndMinusQ_AreTheSameOrientation()
	{
		var q = new TelemetryOrientation(0.5f, 0.5f, 0.5f, 0.5f);

		Assert.AreEqual(0, q.AngleDegreesTo(new TelemetryOrientation(-0.5f, -0.5f, -0.5f, -0.5f)), 1e-4);
	}

	[TestMethod]
	public void AngleDegreesTo_QuarterTurnAroundX_Is90()
	{
		double h = Math.Sqrt(0.5);

		Assert.AreEqual(90, TelemetryOrientation.TryCreate(1, 0, 0, 0)!.Value.AngleDegreesTo(TelemetryOrientation.TryCreate(h, h, 0, 0)!.Value), 1e-4);
	}
}
