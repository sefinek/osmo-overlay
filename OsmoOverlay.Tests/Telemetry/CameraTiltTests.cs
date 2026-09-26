using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Telemetry;

[TestClass]
public sealed class CameraTiltTests
{
	private const double G = 9.80665;
	private const double Hz = 60;

	/// <summary>
	///     `seconds` of samples with the accelerometer reading (x, y, z) in g, driving at speedKmh(t) along headingDegrees(t)
	///     (degrees clockwise from north - a right turn counts up).
	/// </summary>
	private static (List<TelemetryFrame> Frames, double[] Speeds, double[] Headings) Drive(double seconds, double x, double y, double z,
		Func<double, double> speedKmh, Func<double, double> headingDegrees, int gapAt = -1)
	{
		var count = (int)(seconds * Hz) + 1;
		List<TelemetryFrame> frames =
		[
			.. Enumerable.Range(0, count).Select(i => new TelemetryFrame(i, i / Hz, 50, 20, 200, null, x, y, z, StartsAfterGap: i == gapAt))
		];
		return (frames, [.. frames.Select(f => speedKmh(f.SampleTimeSeconds))],
			[.. frames.Select(f => AngleMath.NormalizeDegrees(headingDegrees(f.SampleTimeSeconds)))]);
	}

	private static (double Roll, double Pitch) Middle((List<TelemetryFrame> Frames, double[] Speeds, double[] Headings) drive)
	{
		var (roll, pitch) = CameraTilt.Compute(drive.Frames, drive.Speeds, drive.Headings);
		return (roll[roll.Length / 2], pitch[pitch.Length / 2]);
	}

	[TestMethod]
	public void LevelAtRest_ReadsZero()
	{
		var (roll, pitch) = Middle(Drive(4, 0, 0, -1, _ => 0, _ => 0));

		Assert.AreEqual(0, roll, 1e-9);
		Assert.AreEqual(0, pitch, 1e-9);
	}

	[TestMethod]
	public void TiltedAtRest_ReadsTheTilt()
	{
		var roll = Middle(Drive(4, 0, -Math.Sin(20 * Math.PI / 180), -Math.Cos(20 * Math.PI / 180), _ => 0, _ => 0)).Roll;
		var pitch = Middle(Drive(4, Math.Sin(15 * Math.PI / 180), 0, -Math.Cos(15 * Math.PI / 180), _ => 0, _ => 0)).Pitch;

		Assert.AreEqual(20, roll, 1e-6, "right lean is positive");
		Assert.AreEqual(15, pitch, 1e-6, "nose up is positive");
	}

	[TestMethod]
	public void CarInASteadyTurn_ReadsLevel()
	{
		// 36 km/h turning right at 10 degrees a second: 10 m/s * 0.1745 rad/s = 0.178 g of cornering force, which a car
		// (not leaning) feels sideways.
		var k = 10 * (10 * Math.PI / 180) / G;
		var roll = Middle(Drive(4, 0, k, -1, _ => 36, t => 10 * t)).Roll;

		Assert.AreEqual(0, roll, 0.2);
	}

	[TestMethod]
	public void BikeLeaningIntoATurn_ReadsItsLean()
	{
		// A bike leans until the cornering force lines up with it - the sensor then feels nothing sideways.
		var k = 10 * (10 * Math.PI / 180) / G;
		var roll = Middle(Drive(4, 0, 0, -Math.Sqrt(1 + k * k), _ => 36, t => 10 * t)).Roll;
		var leftRoll = Middle(Drive(4, 0, 0, -Math.Sqrt(1 + k * k), _ => 36, t => -10 * t)).Roll;

		Assert.AreEqual(Math.Atan(k) * 180 / Math.PI, roll, 0.2, "leans right into a right turn");
		Assert.AreEqual(-Math.Atan(k) * 180 / Math.PI, leftRoll, 0.2);
	}

	[TestMethod]
	public void TurnAcrossNorth_IsTheSameTurn()
	{
		var k = 10 * (10 * Math.PI / 180) / G;
		var roll = Middle(Drive(4, 0, 0, -Math.Sqrt(1 + k * k), _ => 36, t => 340 + 10 * t)).Roll;

		Assert.AreEqual(Math.Atan(k) * 180 / Math.PI, roll, 0.2);
	}

	[TestMethod]
	public void Braking_DoesNotReadAsNoseDown()
	{
		// -3 m/s^2 felt along the camera's forward axis, the camera itself level.
		var k = -3 / G;
		var pitch = Middle(Drive(4, k, 0, -1, t => 50 - 3 * 3.6 * t, _ => 0)).Pitch;

		Assert.AreEqual(0, pitch, 0.2);
	}

	[TestMethod]
	public void AtWalkingPace_NothingIsTakenOut()
	{
		// Heading is mostly noise this slow - the reading is shown as it is.
		var roll = Middle(Drive(4, 0, 0.1, -1, _ => 3, t => 30 * t)).Roll;

		Assert.AreEqual(-Math.Asin(0.1 / Math.Sqrt(1.01)) * 180 / Math.PI, roll, 1e-6);
	}

	[TestMethod]
	public void WithoutAGpsFix_NothingIsTakenOut()
	{
		var drive = Drive(4, -0.3, 0, -1, t => 50 - 3 * 3.6 * t, _ => 0);
		drive.Frames = [.. drive.Frames.Select(f => f with { HasGpsFix = false })];

		Assert.AreEqual(Math.Asin(-0.3 / Math.Sqrt(1.09)) * 180 / Math.PI, Middle(drive).Pitch, 1e-6);
	}

	[TestMethod]
	public void SpeedChangeAcrossAGapBetweenFiles_IsNotAcceleration()
	{
		// Standing still in the first file, 40 km/h from the first frame of the next.
		const int gapAt = 120;
		var drive = Drive(4, 0, 0, -1, t => t < gapAt / Hz ? 0 : 40, _ => 0, gapAt);

		var (_, pitch) = CameraTilt.Compute(drive.Frames, drive.Speeds, drive.Headings);

		Assert.AreEqual(0, pitch[gapAt], 1e-9);
		Assert.AreEqual(0, pitch[gapAt - 1], 1e-9);
	}
}
