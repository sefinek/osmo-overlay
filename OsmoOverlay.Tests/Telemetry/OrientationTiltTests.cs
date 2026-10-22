using OsmoOverlay.Cameras.Dji;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Telemetry;

/// <summary>OrientationTilt through DJI's axes: the accelerometer reads -(R20, R21, R22) of the orientation's rotation matrix at rest.</summary>
[TestClass]
public sealed class OrientationTiltTests
{
	private const double Hz = 50;
	private static readonly DjiOsmoFormat Dji = new();

	private static TelemetryOrientation About(char axis, double degrees)
	{
		double half = AngleMath.DegToRad(degrees) / 2;
		(double s, double c) = Math.SinCos(half);
		return TelemetryOrientation.TryCreate(c, axis == 'x' ? s : 0, axis == 'y' ? s : 0, axis == 'z' ? s : 0)!.Value;
	}

	private static TelemetryOrientation Compose(TelemetryOrientation a, TelemetryOrientation b)
	{
		return TelemetryOrientation.TryCreate(
			a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
			a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
			a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
			a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W)!.Value;
	}

	/// <summary>A frame the camera held at `orientation` and felt `dynamic` (g, in the accelerometer's axes) on top of gravity.</summary>
	private static TelemetryFrame Frame(int i, TelemetryOrientation? orientation, (double X, double Y, double Z) dynamic = default)
	{
		(double ax, double ay, double az) = (dynamic.X, dynamic.Y, dynamic.Z);
		if (orientation is { } q)
		{
			ax += -2 * (q.X * q.Z - q.Y * q.W);
			ay += -2 * (q.Y * q.Z + q.X * q.W);
			az += -(1 - 2 * (q.X * q.X + q.Y * q.Y));
		}
		else
		{
			az += -1;
		}

		return new TelemetryFrame(i, i / Hz, 50, 20, 200, null, ax, ay, az, Orientation: orientation);
	}

	private static List<TelemetryFrame> Held(TelemetryOrientation orientation, int count = 200, (double X, double Y, double Z) dynamic = default)
	{
		return [.. Enumerable.Range(0, count).Select(i => Frame(i, orientation, dynamic))];
	}

	[TestMethod]
	[DataRow('x', 25.0)]
	[DataRow('y', -40.0)]
	[DataRow('z', 70.0)]
	public void DjiOrientationGravity_AgreesWithTheAccelerometerAtRest(char axis, double degrees)
	{
		TelemetryOrientation orientation = Compose(About('y', 15), About(axis, degrees));
		TelemetryFrame frame = Frame(0, orientation);

		Direction measured = Dji.Gravity(frame)!.Value;
		Direction oriented = Dji.OrientationGravity(frame)!.Value;

		Assert.AreEqual(measured.X, oriented.X, 1e-6);
		Assert.AreEqual(measured.Y, oriented.Y, 1e-6);
		Assert.AreEqual(measured.Z, oriented.Z, 1e-6);
	}

	[TestMethod]
	public void DjiOrientationGravity_WithoutAnOrientation_IsNull()
	{
		Assert.IsNull(Dji.OrientationGravity(Frame(0, null)));
	}

	[TestMethod]
	public void Quaternion_RollAndPitchAreTheOrientations_WithoutGps()
	{
		IReadOnlyList<DerivedFrame> roll = OrientationTilt.Apply(TelemetryProcessor.Process(Held(About('x', 25)), Dji), Dji, OrientationSource.Quaternion);
		IReadOnlyList<DerivedFrame> pitch = OrientationTilt.Apply(TelemetryProcessor.Process(Held(About('y', -40)), Dji), Dji, OrientationSource.Quaternion);

		Assert.AreEqual(25, roll[^1].RollDegrees, 1e-3);
		Assert.AreEqual(-40, pitch[^1].PitchDegrees, 1e-3);
	}

	[TestMethod]
	public void Quaternion_TurningTheCameraIsNoForce_WhereTheAccelerometerReadsOne()
	{
		List<TelemetryFrame> frames =
		[
			.. Enumerable.Range(0, 400).Select(i => Frame(i, About('y', -90 + 180.0 * i / 399)))
		];

		List<DerivedFrame> accelerometer = TelemetryProcessor.Process(frames, Dji);
		IReadOnlyList<DerivedFrame> quaternion = OrientationTilt.Apply(accelerometer, Dji, OrientationSource.Quaternion);

		Assert.IsGreaterThan(0.5, accelerometer.Max(f => Math.Abs(f.LongitudinalAccelG)), "the accelerometer's gravity moves through the whole range");
		Assert.IsLessThan(1e-5, quaternion.Max(f => Math.Abs(f.LongitudinalAccelG)));
		Assert.IsLessThan(1e-5, quaternion.Max(f => Math.Abs(f.LateralAccelG)));
	}

	[TestMethod]
	public void Quaternion_ARealForceStaysInTheGMeter()
	{
		IReadOnlyList<DerivedFrame> frames = OrientationTilt.Apply(
			TelemetryProcessor.Process(Held(About('x', 10), 50, (0, 0.4, 0)), Dji), Dji, OrientationSource.Quaternion);

		Assert.AreEqual(0.4, Math.Abs(frames[^1].LateralAccelG), 1e-6);
		Assert.AreEqual(0, frames[^1].LongitudinalAccelG, 1e-6);
	}

	[TestMethod]
	public void AccelerometerSource_LeavesTheFramesAsTheyWere()
	{
		List<DerivedFrame> frames = TelemetryProcessor.Process(Held(About('x', 25)), Dji);

		Assert.AreSame(frames, OrientationTilt.Apply(frames, Dji, OrientationSource.Accelerometer));
	}

	[TestMethod]
	public void Quaternion_WithoutAnyOrientation_LeavesTheFramesAsTheyWere()
	{
		List<DerivedFrame> frames = TelemetryProcessor.Process([.. Enumerable.Range(0, 50).Select(i => Frame(i, null))], Dji);

		Assert.AreSame(frames, OrientationTilt.Apply(frames, Dji, OrientationSource.Quaternion));
	}

	[TestMethod]
	public void Quaternion_AFrameWithoutOrientation_KeepsTheAccelerometersTiltAndNoForce()
	{
		TelemetryOrientation level = About('x', 0);
		List<TelemetryFrame> frames = [.. Enumerable.Range(0, 100).Select(i => Frame(i, i == 99 ? null : level))];

		IReadOnlyList<DerivedFrame> result = OrientationTilt.Apply(TelemetryProcessor.Process(frames, Dji), Dji, OrientationSource.Quaternion);

		Assert.AreEqual(0, result[^1].RollDegrees, 1e-6);
		Assert.AreEqual(0, result[^1].LateralAccelG, 1e-9);
		Assert.AreEqual(0, result[^1].LongitudinalAccelG, 1e-9);
	}

	[TestMethod]
	public void Process_WithTheQuaternionSource_IsTheAccelerometersFramesWithItApplied()
	{
		List<TelemetryFrame> frames = [.. Enumerable.Range(0, 300).Select(i => Frame(i, Compose(About('y', 30.0 * i / 299), About('x', 12))))];

		List<DerivedFrame> direct = TelemetryProcessor.Process(frames, Dji, orientationSource: OrientationSource.Quaternion);
		IReadOnlyList<DerivedFrame> applied = OrientationTilt.Apply(TelemetryProcessor.Process(frames, Dji), Dji, OrientationSource.Quaternion);

		CollectionAssert.AreEqual(applied.ToList(), direct);
	}

	[TestMethod]
	public void Effective_IsTheQuaternionOnlyWhereTheCameraReportsAnOrientation()
	{
		TelemetryFrame[] with = [Frame(0, About('x', 5))];
		TelemetryFrame[] without = [Frame(0, null)];

		Assert.AreEqual(OrientationSource.Quaternion, OrientationTilt.Effective(with, Dji, OrientationSource.Quaternion));
		Assert.AreEqual(OrientationSource.Accelerometer, OrientationTilt.Effective(with, Dji, OrientationSource.Accelerometer));
		Assert.AreEqual(OrientationSource.Accelerometer, OrientationTilt.Effective(without, Dji, OrientationSource.Quaternion));
		Assert.AreEqual(OrientationSource.Accelerometer, OrientationTilt.Effective(with, null, OrientationSource.Quaternion));
		Assert.AreEqual(OrientationSource.Accelerometer, OrientationTilt.Effective([], Dji, OrientationSource.Quaternion));
	}

	[TestMethod]
	public void TheSetting_IsTheAccelerometerUnlessChosen()
	{
		Assert.AreEqual(OrientationSource.Accelerometer, new OverlaySettings().OrientationSource);
	}
}
