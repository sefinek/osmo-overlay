using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Telemetry;

[TestClass]
public sealed class TripStatsTests
{
	/// <summary>One frame a second with the given speeds and altitudes; distance is what the speed covers.</summary>
	private static List<DerivedFrame> Ride(double[] speedsKmh, double[] altitudes, int cutAt = -1)
	{
		List<DerivedFrame> frames = [];
		double distance = 0;
		for (var i = 0; i < speedsKmh.Length; i++)
		{
			if (i > 0) distance += speedsKmh[i] / 3.6;
			var raw = new TelemetryFrame(i, i, 50, 20, altitudes[i], null, 0, 0, -1);
			frames.Add(new DerivedFrame(raw, speedsKmh[i], 0, 0, distance, 0, 0, default, 0, 0, 1, i == cutAt));
		}

		return frames;
	}

	private static double[] Repeat(double value, int count)
	{
		return [.. Enumerable.Repeat(value, count)];
	}

	[TestMethod]
	public void MaxSpeed_IsTheHighestSoFar()
	{
		TripStats stats = TripStats.Compute(Ride([10, 30, 20, 40, 5], Repeat(100, 5)));

		CollectionAssert.AreEqual(new double[] { 10, 30, 30, 40, 40 }, Enumerable.Range(0, 5).Select(stats.MaxSpeedKmh).ToArray());
	}

	[TestMethod]
	public void MovingTime_LeavesOutStops_AndAverageIsOverIt()
	{
		// 3 s at 36 km/h, 3 s standing, 2 s at 36 km/h.
		TripStats stats = TripStats.Compute(Ride([36, 36, 36, 36, 0, 0, 0, 36, 36], Repeat(100, 9)));

		Assert.AreEqual(5, stats.MovingSeconds(8), 1e-9);
		Assert.AreEqual(36, stats.AverageSpeedKmh(8), 1e-9);
	}

	[TestMethod]
	public void Average_IsZeroBeforeASecondOfMovement()
	{
		TripStats stats = TripStats.Compute(Ride([0, 0, 50], Repeat(100, 3)));

		Assert.AreEqual(0, stats.AverageSpeedKmh(1));
	}

	[TestMethod]
	public void ElevationGain_IgnoresGpsWobbleBelowTheThreshold()
	{
		// +-2 m of noise on flat ground - a plain sum of every rise would report 12 m.
		TripStats stats = TripStats.Compute(Ride(Repeat(20, 7), [100, 102, 100, 102, 100, 102, 100]));

		Assert.AreEqual(0, stats.ElevationGainMeters(6));
		Assert.AreEqual(0, stats.ElevationLossMeters(6));
	}

	[TestMethod]
	public void ElevationGainAndLoss_CountRealClimbs()
	{
		TripStats stats = TripStats.Compute(Ride(Repeat(20, 7), [100, 104, 110, 108, 104, 100, 101]));

		Assert.AreEqual(10, stats.ElevationGainMeters(6), 1e-9);
		Assert.AreEqual(10, stats.ElevationLossMeters(6), 1e-9);
		Assert.AreEqual(4, stats.ElevationGainMeters(1), 1e-9, "counts as it goes");
	}

	[TestMethod]
	public void NothingIsCountedAcrossACut()
	{
		// The cut-out part climbed 50 m and took the time between the two pieces.
		TripStats stats = TripStats.Compute(Ride([36, 36, 36, 36], [100, 100, 150, 150], 2));

		Assert.AreEqual(0, stats.ElevationGainMeters(3));
		Assert.AreEqual(2, stats.MovingSeconds(3), 1e-9);
	}
}
