using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Tests.Telemetry.External;

[TestClass]
public sealed class ExternalTrackMergeTests
{
	private static readonly DateTime T0 = new(2026, 10, 21, 14, 20, 49, DateTimeKind.Utc);
	private const double Fps = 25;

	private static List<TelemetryFrame> Frames(int count, bool cameraFix = false)
	{
		return [.. Enumerable.Range(0, count).Select(i => new TelemetryFrame(i, i / Fps, cameraFix ? 10 : 0, cameraFix ? 20 : 0, 0, null, 0.1, 0.2, 0.9, Iso: 250f, HasGpsFix: cameraFix))];
	}

	private static ExternalTrackPoint Point(int second, double latitude, double? speed = null)
	{
		return new ExternalTrackPoint(T0.AddSeconds(second), latitude, 18.9, 80 + second, speed);
	}

	[TestMethod]
	public void Apply_TheTracksPositionSpeedAndTimeReplaceTheCamerasAndTheRestStays()
	{
		var track = new ExternalTrack([Point(0, 53.1, 2), Point(1, 53.1001, 4), Point(2, 53.1002, 4), Point(3, 53.1003, 4)]);

		List<TelemetryFrame> merged = ExternalTrackMerge.Apply(Frames(50), track, T0);

		TelemetryFrame frame = merged[37];
		Assert.IsTrue(frame.HasGpsFix);
		Assert.AreEqual(53.1 + 1.48 * 0.0001, frame.Latitude, 1e-9);
		Assert.AreEqual(18.9, frame.Longitude, 1e-9);
		Assert.AreEqual(81.48, frame.AltitudeMeters, 1e-9);
		Assert.AreEqual(4, frame.GpsSpeedMs!.Value, 1e-9);
		Assert.AreEqual(T0.AddSeconds(1.48), frame.GpsTimestamp, "the exact track clock follows each video frame");
		Assert.AreEqual(0.9, frame.AccelZ, "the camera's own readings are kept");
		Assert.AreEqual(250f, frame.Iso);
		Assert.AreEqual(37, frame.FrameNumber);
	}

	[TestMethod]
	public void Apply_ExternalSensorsFollowTheSameAnchorsWithoutReplacingImuTemperature()
	{
		var track = new ExternalTrack(
			[new ExternalTrackPoint(T0, 50, 20, TemperatureCelsius: 20, DistanceMeters: 100), new ExternalTrackPoint(T0.AddSeconds(1), 50.00001, 20, TemperatureCelsius: 22, DistanceMeters: 104)],
			batteries: [new ExternalBatteryReading(T0, 43)]);
		List<TelemetryFrame> camera = [.. Frames(75).Select(f => f with { ImuTemperatureCelsius = 30 })];
		List<TelemetryFrame> merged = ExternalTrackMerge.Apply(camera, track, T0);
		Assert.AreEqual(21.04, merged[13].AmbientTemperatureCelsius!.Value, 1e-9);
		Assert.AreEqual(102.08, merged[13].DeviceDistanceMeters!.Value, 1e-9);
		Assert.AreEqual(43, merged[13].DeviceBatteryPercent);
		Assert.AreEqual(30, merged[13].ImuTemperatureCelsius);
		Assert.IsNull(merged[50].AmbientTemperatureCelsius);
		Assert.IsNull(merged[50].DeviceDistanceMeters);
		Assert.IsNull(merged[50].DeviceBatteryPercent);
		Assert.IsNull(camera[13].AmbientTemperatureCelsius);
	}

	[TestMethod]
	public void Apply_TheOffsetIsTheTrackTimeOfTheFirstFrame()
	{
		var track = new ExternalTrack([Point(0, 53.1), Point(10, 53.2), Point(20, 53.3)]);

		List<TelemetryFrame> merged = ExternalTrackMerge.Apply(Frames(50), track, T0.AddSeconds(10));

		Assert.AreEqual(53.2, merged[0].Latitude, 1e-9);
		Assert.AreEqual(T0.AddSeconds(10), merged[0].GpsTimestamp);
	}

	[TestMethod]
	public void Apply_ATimerPauseHoldsPositionWithoutInventingAFixOrSpeed()
	{
		var track = new ExternalTrack([Point(0, 53.1, 2), Point(1, 53.1001, 2), Point(31, 53.10015, 2), Point(32, 53.1002, 2)], [new ExternalTrackPause(T0.AddSeconds(1), T0.AddSeconds(31))]);

		List<TelemetryFrame> merged = ExternalTrackMerge.Apply(Frames(25 * 32), track, T0);

		TelemetryFrame inStop = merged[25 * 15];
		Assert.IsFalse(inStop.HasGpsFix);
		Assert.IsNull(inStop.GpsSpeedMs);
		Assert.AreEqual(53.1001, inStop.Latitude, 1e-9);
		Assert.AreEqual(T0.AddSeconds(15), inStop.GpsTimestamp, "a pause does not shorten elapsed time");
	}

	[TestMethod]
	public void Apply_ALostSignalLeavesNoFixAndHoldsThePosition()
	{
		var track = new ExternalTrack([Point(0, 53.1), Point(1, 53.1001), Point(31, 53.2), Point(32, 53.2001)]);

		List<TelemetryFrame> merged = ExternalTrackMerge.Apply(Frames(25 * 32), track, T0);

		TelemetryFrame lost = merged[25 * 15];
		Assert.IsFalse(lost.HasGpsFix);
		Assert.AreEqual(53.1001, lost.Latitude, 1e-5, "the last position the track gave");
		Assert.IsNull(lost.GpsSpeedMs);
		Assert.AreEqual(T0.AddSeconds(15), lost.GpsTimestamp, "the clock goes on");
		Assert.IsTrue(merged[25 * 31 + 5].HasGpsFix);
	}

	[TestMethod]
	public void Apply_FramesBeforeTheTrackHoldItsFirstPositionAndAfterItsLast_WithoutAFix()
	{
		var track = new ExternalTrack([Point(0, 53.1), Point(1, 53.1001), Point(2, 53.1002)]);

		List<TelemetryFrame> merged = ExternalTrackMerge.Apply(Frames(25 * 5), track, T0.AddSeconds(1));

		Assert.IsTrue(merged[0].HasGpsFix);
		Assert.IsFalse(merged[25 * 2].HasGpsFix);
		Assert.AreEqual(53.1002, merged[25 * 4].Latitude, 1e-9, "after the end the last fix is held, not (0, 0)");

		List<TelemetryFrame> early = ExternalTrackMerge.Apply(Frames(25 * 5), track, T0.AddSeconds(-2));

		Assert.IsFalse(early[0].HasGpsFix);
		Assert.AreEqual(53.1, early[0].Latitude, 1e-9, "before the start the first fix is held, not (0, 0)");
		Assert.IsTrue(early[25 * 2].HasGpsFix);
	}

	[TestMethod]
	public void Apply_FramesTheTrackDoesntReachKeepTheCamerasOwnGps()
	{
		var track = new ExternalTrack([Point(0, 53.1), Point(1, 53.1001)]);

		List<TelemetryFrame> merged = ExternalTrackMerge.Apply(Frames(25 * 3, true), track, T0);

		Assert.AreEqual(53.1, merged[0].Latitude, 1e-9);
		Assert.AreEqual(10, merged[25 * 2].Latitude, "the camera's");
		Assert.AreEqual(20, merged[25 * 2].Longitude);
		Assert.IsTrue(merged[25 * 2].HasGpsFix);
	}

	[TestMethod]
	public void Apply_TheMergedFramesGoThroughTheProcessorLikeAnyOthers()
	{
		var track = new ExternalTrack(Enumerable.Range(0, 30).Select(s => Point(s, 53.1 + s * 5 * 9e-6, 5)));

		IReadOnlyList<DerivedFrame> derived = TelemetryProcessor.Process(ExternalTrackMerge.Apply(Frames(25 * 29), track, T0), null);

		Assert.AreEqual(18, derived[25 * 20].SpeedKmh, 0.5, "5 m/s from the track");
		Assert.IsTrue(derived[^1].CumulativeDistanceMeters is > 120 and < 150);
	}
}
