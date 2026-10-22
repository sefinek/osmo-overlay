using System.Text.Json.Nodes;
using OsmoOverlay.Cameras.GoPro;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Cameras.GoPro;

[TestClass]
public sealed class GoProFormatTests
{
	private static JsonArray Streams(params string[] streams)
	{
		return JsonNode.Parse($"[{string.Join(',', streams)}]")!.AsArray();
	}

	private static string Video(int index, bool thumbnail = false)
	{
		return $"{{\"index\":{index},\"codec_type\":\"video\",\"disposition\":{{\"attached_pic\":{(thumbnail ? 1 : 0)}}}}}";
	}

	private const string Gpmd = """{"index":3,"codec_type":"data","codec_tag_string":"gpmd"}""";

	private static TelemetryFrame Frame(double accelX, double accelY, double accelZ, TelemetryOrientation? orientation = null)
	{
		return new TelemetryFrame(0, 0, 0, 0, 0, null, accelX, accelY, accelZ, Orientation: orientation);
	}

	[TestMethod]
	public void Detect_AFileWithAGpmdStream()
	{
		CameraRecording? recording = new GoProFormat().Detect("x.mp4", Streams(Video(0), Gpmd));

		Assert.IsNotNull(recording);
		Assert.AreEqual(3, recording.TelemetryStream);
		Assert.IsNull(recording.Lenses);
	}

	[TestMethod]
	public void Detect_NotAFileWithoutOneOrWithTwoLensesOrAThumbnailOnly()
	{
		var gopro = new GoProFormat();

		Assert.IsNull(gopro.Detect("x.mp4", Streams(Video(0), """{"index":3,"codec_type":"data","codec_tag_string":"djmd"}""")));
		Assert.IsNull(gopro.Detect("x.360", Streams(Video(0), Video(1), Gpmd)), "a 360 recording isn't read");
		Assert.IsNotNull(gopro.Detect("x.mp4", Streams(Video(0), Video(1, true), Gpmd)), "a thumbnail isn't a lens");
	}

	[TestMethod]
	public void SupportNotice_OnlyForAModelItWasNotTestedOn()
	{
		var gopro = new GoProFormat();

		Assert.IsNull(gopro.SupportNotice("HERO12 Black"));
		Assert.IsNull(gopro.SupportNotice("hero12 black"));
		Assert.IsTrue(gopro.SupportNotice("HERO10 Black")!.Contains("HERO10 Black"));
		Assert.IsFalse(string.IsNullOrWhiteSpace(gopro.SupportNotice(null)));
	}

	[TestMethod]
	public void Gravity_IsXRightYDownZForwardFromTheSensorsAxes()
	{
		var gopro = new GoProFormat();

		Direction level = gopro.Gravity(Frame(0, 0, 1))!.Value;
		Assert.AreEqual(new Direction(0, 1, 0), level, "lying level the sensor feels up, gravity pulls down");

		// Pointed at the floor the sensor feels the reaction backward (AccelY), and gravity pulls forward.
		Direction pointedDown = gopro.Gravity(Frame(0, 0.866, 0.5))!.Value;
		Assert.AreEqual(0.866, pointedDown.Z, 1e-12);
		Assert.AreEqual(0.5, pointedDown.Y, 1e-12);
	}

	[TestMethod]
	public void Tilt_PitchUpIsPositiveAndALeanToTheRightIsPositive()
	{
		var gopro = new GoProFormat();

		DerivedFrame level = TelemetryProcessor.Process([Frame(0, 0, 1)], gopro)[0];
		DerivedFrame noseUp = TelemetryProcessor.Process([Frame(0, -0.342, 0.940)], gopro)[0];
		DerivedFrame noseDown = TelemetryProcessor.Process([Frame(0, 0.342, 0.940)], gopro)[0];
		DerivedFrame leaning = TelemetryProcessor.Process([Frame(0.342, 0, 0.940)], gopro)[0];

		Assert.AreEqual(0, level.RollDegrees, 1e-9);
		Assert.AreEqual(0, level.PitchDegrees, 1e-9);
		Assert.AreEqual(20, noseUp.PitchDegrees, 0.01);
		Assert.AreEqual(-20, noseDown.PitchDegrees, 0.01);
		Assert.AreEqual(20, leaning.RollDegrees, 0.01, "along its left axis the sensor feels the reaction to a lean to the right");
	}

	[TestMethod]
	public void OrientationGravity_IsTheGravityVectorTheOrientationWasMadeFrom()
	{
		var gopro = new GoProFormat();
		double length = Math.Sqrt(0.2 * 0.2 + 0.9 * 0.9 + 0.38 * 0.38);
		Direction gravity = new(0.2 / length, 0.9 / length, -0.38 / length);
		TelemetryOrientation orientation = GoProTilt.Orientation(gravity)!.Value;

		Direction? back = gopro.OrientationGravity(Frame(0, 0, 1, orientation));

		Assert.AreEqual(gravity.X, back!.Value.X, 1e-6);
		Assert.AreEqual(gravity.Y, back.Value.Y, 1e-6);
		Assert.AreEqual(gravity.Z, back.Value.Z, 1e-6);
		Assert.IsNull(gopro.OrientationGravity(Frame(0, 0, 1)));
	}

	[TestMethod]
	public void GoProTilt_StraightDownIsNoRotationAndStraightUpAHalfTurn()
	{
		Direction down = GoProTilt.Gravity(GoProTilt.Orientation(new Direction(0, 1, 0))!.Value);
		Direction up = GoProTilt.Gravity(GoProTilt.Orientation(new Direction(0, -1, 0))!.Value);

		Assert.AreEqual(0, down.X, 1e-6);
		Assert.AreEqual(1, down.Y, 1e-6);
		Assert.AreEqual(-1, up.Y, 1e-6);
		Assert.AreEqual(0, up.X, 1e-6);
		Assert.AreEqual(0, up.Z, 1e-6);
	}

	[TestMethod]
	public void GoProTilt_ANonVectorIsNoOrientation()
	{
		Assert.IsNull(GoProTilt.Orientation(new Direction(0, 0, 0)));
		Assert.IsNull(GoProTilt.Orientation(new Direction(double.NaN, 1, 0)));
	}

	[TestMethod]
	public void QuaternionSource_TakesGravityFromTheCamerasOwnVector()
	{
		var gopro = new GoProFormat();
		TelemetryOrientation tilted = GoProTilt.Orientation(new Direction(0, 0.866, 0.5))!.Value;
		TelemetryFrame[] frames = [Frame(0, 0.2, 1, tilted)];

		IReadOnlyList<DerivedFrame> derived = OrientationTilt.Apply(TelemetryProcessor.Process(frames, gopro), gopro, OrientationSource.Quaternion);

		Assert.AreEqual(OrientationSource.Quaternion, OrientationTilt.Effective(frames, gopro, OrientationSource.Quaternion));
		Assert.AreEqual(-30, derived[0].PitchDegrees, 0.1, "pointed at the floor from the vector, whatever the accelerometer felt");
	}

	[TestMethod]
	public void DescribeTelemetry_SaysWhetherThereWasAGps()
	{
		var gopro = new GoProFormat();
		TelemetryFrame withFix = Frame(0, 0, 1);
		TelemetryFrame without = withFix with { HasGpsFix = false };

		Assert.AreNotEqual(gopro.DescribeTelemetry([withFix]), gopro.DescribeTelemetry([without]));
	}
}
