using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Telemetry;

[TestClass]
public sealed class SpeedCalibrationTests
{
	[TestMethod]
	public void PercentFor_WorksOutTheCorrectionFromTheKnownSpeed()
	{
		Assert.AreEqual(13.6, SpeedCalibration.PercentFor(25, 22, 0), 1e-9);
		Assert.AreEqual(0.0, SpeedCalibration.PercentFor(22, 22, 0));
		Assert.AreEqual(10.0, SpeedCalibration.PercentFor(22, 20, 0), 1e-9);
	}

	[TestMethod]
	public void PercentFor_NeverTakesTheTopSpeedAboveTheRealOne()
	{
		foreach ((double actual, double shown) in new[] { (25.0, 22.0), (25.0, 22.4), (32.0, 29.7), (45.0, 41.3), (25.0, 24.95) })
		{
			double percent = SpeedCalibration.PercentFor(actual, shown, 0);
			double corrected = SpeedCalibration.Corrected(shown, percent);
			Assert.IsTrue(corrected <= actual + 1e-9, $"{shown} -> {corrected} > {actual}");
			Assert.IsTrue(actual - corrected < shown * SpeedCalibration.Step / 100 + 1e-9, $"{shown} -> {corrected} is more than a step short of {actual}");
		}
	}

	[TestMethod]
	public void PercentFor_StartsFromWhatTheReceiverReadWhenACorrectionIsAlreadyOn()
	{
		Assert.AreEqual(10.0, SpeedCalibration.PercentFor(25, 22.7272, 0), 1e-9);
		Assert.AreEqual(10.0, SpeedCalibration.PercentFor(25, 25, 10), 1e-9);
		Assert.AreEqual(13.6, SpeedCalibration.PercentFor(25, 24.2, 10), 1e-9);
	}

	[TestMethod]
	public void PercentFor_StaysWithinTheRangeAndIgnoresNonsense()
	{
		Assert.AreEqual(SpeedCalibration.MaxPercent, SpeedCalibration.PercentFor(100, 20, 0));
		Assert.AreEqual(0.0, SpeedCalibration.PercentFor(15, 20, 0));
		Assert.AreEqual(0.0, SpeedCalibration.PercentFor(0, 20, 0));
		Assert.AreEqual(0.0, SpeedCalibration.PercentFor(25, double.NaN, 0));
	}

	private static List<DerivedFrame> Speeds(double sampleRate, params double[] kmh)
	{
		return [.. kmh.Select((v, i) => new DerivedFrame(new TelemetryFrame(i, i / sampleRate, 50, 20, 200, null, 0, 0, 1), v, 0, 0, 0, 0, 0,
			default, 0, 0, 0, 0, 0, false))];
	}

	[TestMethod]
	public void SustainedTopSpeed_IgnoresASingleSpike()
	{
		double[] kmh = [.. Enumerable.Repeat(22.0, 100)];
		kmh[50] = 26;

		double sustained = SpeedCalibration.SustainedTopSpeedKmh(Speeds(10, kmh));

		Assert.AreEqual(22.0, sustained, 0.2);
	}

	[TestMethod]
	public void SustainedTopSpeed_FindsTheFastestHeldStretch()
	{
		double[] kmh = [.. Enumerable.Repeat(15.0, 50), .. Enumerable.Repeat(24.0, 40), .. Enumerable.Repeat(18.0, 50)];

		Assert.AreEqual(24.0, SpeedCalibration.SustainedTopSpeedKmh(Speeds(10, kmh)), 1e-9);
	}

	[TestMethod]
	public void SustainedTopSpeed_OfAShortRecordingIsItsAverage_AndZeroWithoutFrames()
	{
		Assert.AreEqual(21.0, SpeedCalibration.SustainedTopSpeedKmh(Speeds(10, 20, 22)), 1e-9);
		Assert.AreEqual(0.0, SpeedCalibration.SustainedTopSpeedKmh([]));
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
