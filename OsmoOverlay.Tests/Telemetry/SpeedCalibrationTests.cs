using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Telemetry;

[TestClass]
public sealed class SpeedCalibrationTests
{
	[TestMethod]
	public void PercentFor_WorksOutTheCorrectionFromTheKnownSpeed()
	{
		Assert.AreEqual(13.5, SpeedCalibration.PercentFor(25, 22, 0));
		Assert.AreEqual(0.0, SpeedCalibration.PercentFor(22, 22, 0));
	}

	[TestMethod]
	public void PercentFor_StartsFromWhatTheReceiverReadWhenACorrectionIsAlreadyOn()
	{
		Assert.AreEqual(10.0, SpeedCalibration.PercentFor(25, 22.727, 0), 0.5);
		Assert.AreEqual(10.0, SpeedCalibration.PercentFor(25, 25, 10), 0.5);
	}

	[TestMethod]
	public void PercentFor_StaysWithinTheRangeAndIgnoresNonsense()
	{
		Assert.AreEqual(SpeedCalibration.MaxPercent, SpeedCalibration.PercentFor(100, 20, 0));
		Assert.AreEqual(0.0, SpeedCalibration.PercentFor(15, 20, 0));
		Assert.AreEqual(0.0, SpeedCalibration.PercentFor(0, 20, 0));
		Assert.AreEqual(0.0, SpeedCalibration.PercentFor(25, double.NaN, 0));
	}

	[TestMethod]
	public void Factor_HoldsThePercentToItsRange()
	{
		Assert.AreEqual(1.1, SpeedCalibration.Factor(10), 1e-9);
		Assert.AreEqual(1.0, SpeedCalibration.Factor(-5));
		Assert.AreEqual(1.5, SpeedCalibration.Factor(400));
	}

	[TestMethod]
	public void Process_RaisesOnlyTheShownSpeed()
	{
		TelemetryFrame[] frames = [.. Enumerable.Range(0, 60).Select(i => new TelemetryFrame(i, i / 30.0, 50 + i * 1e-5, 20, 200, null, 0, 0, 1, GpsSpeedMs: 6))];

		List<DerivedFrame> plain = TelemetryProcessor.Process(frames, null);
		List<DerivedFrame> corrected = TelemetryProcessor.Process(frames, null, false, 10);

		Assert.AreEqual(plain[30].SpeedKmh * 1.1, corrected[30].SpeedKmh, 1e-9);
		Assert.AreEqual(plain[30].CumulativeDistanceMeters, corrected[30].CumulativeDistanceMeters);
		Assert.AreEqual(plain[30].HeadingDegrees, corrected[30].HeadingDegrees);
	}
}
