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
	public void CruisingSpeed_FindsThePlateauNotTheFastestMoment()
	{
		// 10 Hz: two minutes at the limiter (22 km/h, jittering), a minute at 12, and a few seconds at 26 down a hill.
		var random = new Random(7);
		List<double> kmh = [];
		for (int i = 0; i < 1200; i++) kmh.Add(22 + (random.NextDouble() - 0.5) * 1.2);
		for (int i = 0; i < 600; i++) kmh.Add(12 + (random.NextDouble() - 0.5) * 1.2);
		for (int i = 0; i < 60; i++) kmh.Add(26);
		for (int i = 0; i < 600; i++) kmh.Add(22 + (random.NextDouble() - 0.5) * 1.2);

		Assert.AreEqual(22.0, SpeedCalibration.CruisingSpeedKmh(Speeds(10, [.. kmh])), 0.2);
	}

	[TestMethod]
	public void CruisingSpeed_IgnoresStandingStill_AndIsZeroWithoutMoving()
	{
		double[] kmh = [.. Enumerable.Repeat(0.0, 500), .. Enumerable.Repeat(18.0, 300)];

		Assert.AreEqual(18.0, SpeedCalibration.CruisingSpeedKmh(Speeds(10, kmh)), 1e-9);
		Assert.AreEqual(0.0, SpeedCalibration.CruisingSpeedKmh(Speeds(10, 0, 0, 1)));
		Assert.AreEqual(0.0, SpeedCalibration.CruisingSpeedKmh([]));
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
