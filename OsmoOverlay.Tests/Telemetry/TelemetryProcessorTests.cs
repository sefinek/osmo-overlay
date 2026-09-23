using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Telemetry;

[TestClass]
public sealed class TelemetryProcessorTests
{
	// TelemetryMath's earth radius (6 371 000 m) -> meters per degree of latitude.
	private const double MetersPerDegreeLat = 6_371_000 * Math.PI / 180;
	private const double StartLat = 50.0;
	private const double StartLon = 20.0;

	/// <summary>Straight-line motion at `speedMs`, sampled at `hz`, without GPS-measured speed unless given.</summary>
	private static List<TelemetryFrame> Track(double speedMs, double headingDegrees, double hz, double seconds, double? gpsSpeedMs = null)
	{
		var metersPerDegreeLon = MetersPerDegreeLat * Math.Cos(StartLat * Math.PI / 180);
		var rad = headingDegrees * Math.PI / 180;
		return
		[
			.. Enumerable.Range(0, (int)(seconds * hz) + 1).Select(i =>
			{
				var t = i / hz;
				var d = speedMs * t;
				return new TelemetryFrame(i, t, StartLat + d * Math.Cos(rad) / MetersPerDegreeLat,
					StartLon + d * Math.Sin(rad) / metersPerDegreeLon, 200, null, 0, 0, 1, gpsSpeedMs);
			})
		];
	}

	[TestMethod]
	public void Speed_FromPosition_WhenNoGpsSpeed()
	{
		List<DerivedFrame> derived = TelemetryProcessor.Process(Track(10, 0, 10, 5));

		Assert.AreEqual(36, derived[^1].SpeedKmh, 0.5);
	}

	[TestMethod]
	public void Speed_PrefersGpsMeasuredSpeed_OverPosition()
	{
		List<DerivedFrame> derived = TelemetryProcessor.Process(Track(0, 0, 10, 3, gpsSpeedMs: 12.5));

		Assert.AreEqual(45, derived[^1].SpeedKmh, 1e-9);
	}

	[TestMethod]
	[DataRow(10.0)]
	[DataRow(29.97)]
	[DataRow(59.94)]
	public void Speed_DoesNotDependOnSamplingRate(double hz)
	{
		List<DerivedFrame> derived = TelemetryProcessor.Process(Track(8, 45, hz, 4));

		Assert.AreEqual(8 * 3.6, derived[^1].SpeedKmh, 0.3, $"at {hz} Hz");
	}

	[TestMethod]
	[DataRow(0.0)]
	[DataRow(90.0)]
	[DataRow(180.0)]
	[DataRow(270.0)]
	public void Heading_FollowsDirectionOfTravel(double heading)
	{
		List<DerivedFrame> derived = TelemetryProcessor.Process(Track(10, heading, 10, 3));

		var error = Math.Abs(((derived[^1].HeadingDegrees - heading) % 360 + 540) % 360 - 180);
		Assert.IsTrue(error < 0.5, $"heading {derived[^1].HeadingDegrees} vs expected {heading}");
	}

	[TestMethod]
	public void CumulativeDistance_AdvancesInWholeSecondSteps()
	{
		List<DerivedFrame> derived = TelemetryProcessor.Process(Track(10, 30, 59.94, 20));

		// Steps of >= 1 s (GPS jitter at 60 Hz would otherwise inflate it) - so it lags by up to a second of
		// travel, except the last frame, which gets the final partial step so the total is complete.
		Assert.AreEqual(19 * 60 / 59.94 * 10, derived[^2].CumulativeDistanceMeters, 0.5);
		Assert.AreEqual(10 * derived[^1].Raw.SampleTimeSeconds, derived[^1].CumulativeDistanceMeters, 0.2);
		Assert.AreEqual(0, derived[59].CumulativeDistanceMeters, "no step before a full second has passed");
	}

	[TestMethod]
	[DataRow(59.94, 20.0)]
	[DataRow(10.0, 7.35)]
	[DataRow(59.94, 0.5)]
	public void Summary_TotalDistance_IncludesTheLastPartialStep(double hz, double seconds)
	{
		List<DerivedFrame> derived = TelemetryProcessor.Process(Track(10, 30, hz, seconds));
		var travelled = 10 * derived[^1].Raw.SampleTimeSeconds;

		Assert.AreEqual(travelled, TelemetryProcessor.Summarize(derived).TotalDistanceMeters, travelled * 0.001 + 0.01);
	}

	[TestMethod]
	public void Summary_TotalDistance_IgnoresTimeSpentStationaryAtTheEnd()
	{
		List<TelemetryFrame> moving = Track(10, 0, 10, 5.45);
		TelemetryFrame last = moving[^1];
		List<TelemetryFrame> frames =
		[
			.. moving,
			.. Enumerable.Range(1, 50).Select(i => last with { FrameNumber = last.FrameNumber + i, SampleTimeSeconds = last.SampleTimeSeconds + i / 10.0 })
		];

		Assert.AreEqual(54, TelemetryProcessor.Summarize(TelemetryProcessor.Process(frames)).TotalDistanceMeters, 0.1);
	}

	[TestMethod]
	public void Stationary_HoldsLastHeading_AndReportsNoGradient()
	{
		List<TelemetryFrame> moving = Track(10, 90, 10, 3);
		TelemetryFrame last = moving[^1];
		List<TelemetryFrame> frames =
		[
			.. moving,
			.. Enumerable.Range(1, 30).Select(i => last with { FrameNumber = last.FrameNumber + i, SampleTimeSeconds = last.SampleTimeSeconds + i / 10.0 })
		];

		List<DerivedFrame> derived = TelemetryProcessor.Process(frames);

		Assert.AreEqual(90, derived[^1].HeadingDegrees, 0.5);
		Assert.AreEqual(0, derived[^1].SpeedKmh, 1e-9);
		Assert.AreEqual(0, derived[^1].GradientPercent);
	}

	[TestMethod]
	public void GpsLossRanges_AreContiguousRunsWithoutFix()
	{
		List<TelemetryFrame> frames =
		[
			.. Track(5, 0, 10, 2).Select((f, i) => f with { HasGpsFix = i is not (3 or 4 or 5 or 20) })
		];

		List<(double Start, double End)> ranges = TelemetryProcessor.FindGpsLossRanges(frames);

		CollectionAssert.AreEqual(new[] { (0.3, 0.5), (2.0, 2.0) }, ranges.Select(r => (Math.Round(r.Start, 6), Math.Round(r.End, 6))).ToArray());
		Assert.IsTrue(TelemetryProcessor.HasAnyGpsFix(frames));
		Assert.IsFalse(TelemetryProcessor.HasAnyGpsFix([.. frames.Select(f => f with { HasGpsFix = false })]));
	}

	[TestMethod]
	public void FindIndex_ReturnsFirstFrameAtOrAfterTime_ClampedToLast()
	{
		List<DerivedFrame> derived = TelemetryProcessor.Process(Track(1, 0, 10, 1));

		Assert.AreEqual(0, TelemetryProcessor.FindIndex(derived, -5));
		Assert.AreEqual(3, TelemetryProcessor.FindIndex(derived, 0.3));
		Assert.AreEqual(4, TelemetryProcessor.FindIndex(derived, 0.31));
		Assert.AreEqual(derived.Count - 1, TelemetryProcessor.FindIndex(derived, 99));
	}
}

[TestClass]
public sealed class GpsForwardFillTests
{
	[TestMethod]
	public void MissingFix_HoldsLastKnownPosition()
	{
		var fill = new GpsForwardFill();

		fill.Apply(50, 20, 300);
		var (lat, lon, alt, hasFix) = fill.Apply(null, null, null);

		Assert.AreEqual((50.0, 20.0, 300.0, false), (lat, lon, alt, hasFix));
	}

	[TestMethod]
	public void AltitudeWithoutPosition_UpdatesAltitudeOnly()
	{
		var fill = new GpsForwardFill();

		fill.Apply(50, 20, 300);
		var (lat, _, alt, hasFix) = fill.Apply(null, null, 310);

		Assert.AreEqual(50.0, lat);
		Assert.AreEqual(310.0, alt);
		Assert.IsFalse(hasFix);
	}

	[TestMethod]
	public void NoFixYet_IsZeroButFlaggedAsNoFix()
	{
		var (lat, lon, _, hasFix) = new GpsForwardFill().Apply(null, null, null);

		Assert.AreEqual((0.0, 0.0, false), (lat, lon, hasFix));
	}
}
