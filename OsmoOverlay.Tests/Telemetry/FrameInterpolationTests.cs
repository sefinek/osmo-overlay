using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Telemetry;

[TestClass]
public sealed class FrameInterpolationTests
{
	private static DerivedFrame Frame(double seconds, double speed, double yaw = 0, bool gap = false, bool fix = true)
	{
		var raw = new TelemetryFrame(0, seconds, 50 + speed / 1000, 20, 100 + speed, null, 0, 0, 1, HasGpsFix: fix, StartsAfterGap: gap,
			DroneAttitude: new DroneAttitude(0, 0, yaw), DroneSticks: new DroneSticks(speed / 100, 0, 0, 0));
		return new DerivedFrame(raw, speed, yaw, 0, speed, 0, 0, default, 0, 0, 1, 0, 0);
	}

	[TestMethod]
	public void BetweenTwoFrames_NumbersRunOnAndTheTimeIsTheAskedOne()
	{
		DerivedFrame[] frames = [Frame(0, 10), Frame(0.1, 20)];

		DerivedFrame middle = FrameInterpolation.At(frames, 0.025);

		Assert.AreEqual(12.5, middle.SpeedKmh, 1e-9);
		Assert.AreEqual(112.5, middle.Raw.AltitudeMeters, 1e-9);
		Assert.AreEqual(0.125, middle.Raw.DroneSticks!.Aileron, 1e-9);
		Assert.AreEqual(0.025, middle.Raw.SampleTimeSeconds, 1e-12);
	}

	[TestMethod]
	public void TheHeadingTakesTheShortWayRoundNorth()
	{
		DerivedFrame[] frames = [Frame(0, 10, 350), Frame(0.1, 10, 10)];

		double yaw = FrameInterpolation.At(frames, 0.05).Raw.DroneAttitude!.YawDegrees;

		Assert.IsTrue(yaw is > 359.9 or < 0.1, yaw.ToString());
	}

	[TestMethod]
	public void OnAFrame_ItIsThatFrame()
	{
		DerivedFrame[] frames = [Frame(0, 10), Frame(0.1, 20), Frame(0.2, 30)];

		Assert.AreSame(frames[1], FrameInterpolation.At(frames, 0.1));
		Assert.AreSame(frames[0], FrameInterpolation.At(frames, -1));
		Assert.AreSame(frames[2], FrameInterpolation.At(frames, 5));
	}

	[TestMethod]
	public void AcrossAGapOrALongSpan_NothingIsCarriedAcross()
	{
		DerivedFrame[] afterGap = [Frame(0, 10), Frame(0.1, 20, gap: true)];
		DerivedFrame[] sparse = [Frame(0, 10), Frame(2, 20)];

		Assert.AreSame(afterGap[1], FrameInterpolation.At(afterGap, 0.05));
		Assert.AreSame(sparse[1], FrameInterpolation.At(sparse, 1));
	}

	[TestMethod]
	public void WithoutAFixOnBothSides_ThePositionIsTheNearestOnes()
	{
		DerivedFrame[] frames = [Frame(0, 10, fix: false), Frame(0.1, 20)];

		DerivedFrame frame = FrameInterpolation.At(frames, 0.09);

		Assert.AreEqual(frames[1].Raw.Latitude, frame.Raw.Latitude);
	}

	[TestMethod]
	public void SticksAndSpeed_GoThroughTheFramesAroundAsACurve_NotAsLines()
	{
		DerivedFrame[] frames = [Frame(0, 0), Frame(0.1, 0), Frame(0.2, 10), Frame(0.3, 10)];

		DerivedFrame early = FrameInterpolation.At(frames, 0.125);
		DerivedFrame middle = FrameInterpolation.At(frames, 0.15);

		Assert.AreEqual(5, middle.SpeedKmh, 1e-9, "symmetric, so halfway is halfway");
		Assert.IsTrue(early.SpeedKmh < 2.5, "it eases out of the flat stretch instead of leaving it at the line's slope");
		Assert.IsTrue(early.Raw.DroneSticks!.Aileron < 0.025);
	}

	[TestMethod]
	public void ASpeedThatEasesIsNeverBelowZero_AndSticksStayInRange()
	{
		DerivedFrame[] frames = [Frame(0, 0), Frame(0.1, 0), Frame(0.2, 100), Frame(0.3, 0), Frame(0.4, 0)];

		for (double t = 0; t <= 0.4; t += 0.005)
		{
			DerivedFrame frame = FrameInterpolation.At(frames, t);
			Assert.IsTrue(frame.SpeedKmh >= 0, $"t={t}");
			Assert.IsTrue(Math.Abs(frame.Raw.DroneSticks!.Aileron) <= 1, $"t={t}");
		}
	}
}
