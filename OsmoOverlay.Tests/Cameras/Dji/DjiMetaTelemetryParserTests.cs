using OsmoOverlay.Cameras.Dji;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Cameras.Dji;

[TestClass]
public sealed class DjiMetaTelemetryParserTests
{
	private const double Fps = 60000 / 1001.0;

	[TestMethod]
	public void Parse_DecodesEveryDocumentedField()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(DjmdSample.Stream(new DjmdSample()), Fps);

		TelemetryFrame f = result.Frames.Single();
		Assert.AreEqual(50.061, f.Latitude);
		Assert.AreEqual(19.938, f.Longitude);
		Assert.AreEqual(219.5, f.AltitudeMeters, 1e-9);
		Assert.AreEqual(new DateTime(2026, 9, 23, 17, 59, 25), f.GpsTimestamp);
		Assert.AreEqual(5.0, f.GpsSpeedMs!.Value, 1e-6, "speed is the length of the (vx, vy) velocity vector");
		Assert.AreEqual(100f, f.Iso);
		Assert.AreEqual(1 / 240.0, f.ShutterSeconds!.Value, 1e-12);
		Assert.AreEqual(5600, f.ColorTemperatureKelvin);
		Assert.AreEqual(0.1, f.AccelX, 1e-6);
		Assert.AreEqual(-0.2, f.AccelY, 1e-6);
		Assert.AreEqual(0.98, f.AccelZ, 1e-6);
		Assert.IsTrue(f.HasGpsFix);
		Assert.AreEqual("DJI TEST", result.CameraModel);
	}

	[TestMethod]
	public void Parse_SampleTimeIsIndexOverFps()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(
			DjmdSample.Stream(new DjmdSample(), new DjmdSample(), new DjmdSample()), Fps);

		CollectionAssert.AreEqual(new[] { 0, 1, 2 }, result.Frames.Select(f => f.FrameNumber).ToArray());
		Assert.AreEqual(2 / Fps, result.Frames[2].SampleTimeSeconds, 1e-12);
	}

	[TestMethod]
	public void Parse_NoFix_CarriesLastPositionForward()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(DjmdSample.Stream(
			new DjmdSample { Lat = 50.1, Lon = 19.9, AltitudeMm = 250_000 },
			new DjmdSample { GpsStatus = 1, Lat = 0, Lon = 0 }), Fps);

		TelemetryFrame lost = result.Frames[1];
		Assert.IsFalse(lost.HasGpsFix);
		Assert.AreEqual(50.1, lost.Latitude);
		Assert.AreEqual(19.9, lost.Longitude);
	}

	[TestMethod]
	public void Parse_InvalidGpsStatus_IsNoFixEvenWithCoordinates()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(DjmdSample.Stream(
			new DjmdSample { Lat = 50.1, Lon = 19.9 },
			new DjmdSample { GpsStatus = 1, Lat = 51, Lon = 20 }), Fps);

		Assert.IsFalse(result.Frames[1].HasGpsFix);
		Assert.AreEqual(50.1, result.Frames[1].Latitude);
	}

	[TestMethod]
	public void Parse_RecordedWithoutGps_HasNoFixAndNoTime()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(DjmdSample.Stream(
			new DjmdSample { StatusOnly = true, GpsStatus = 1, Vx = 0, Vy = 0 }), Fps);

		TelemetryFrame frame = result.Frames.Single();
		Assert.IsFalse(frame.HasGpsFix);
		Assert.IsNull(frame.GpsTimestamp);
		Assert.AreEqual((0.0, 0.0), (frame.Latitude, frame.Longitude));
	}

	[TestMethod]
	public void Parse_RtkGpsStatus_IsAFix()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(DjmdSample.Stream(new DjmdSample { GpsStatus = 2 }), Fps);

		Assert.IsTrue(result.Frames[0].HasGpsFix);
	}

	[TestMethod]
	public void Parse_CoordinatesInRadians_AreConvertedToDegrees()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(DjmdSample.Stream(
			new DjmdSample { CoordUnit = 0, Lat = AngleMath.DegToRad(50.061), Lon = AngleMath.DegToRad(19.938) }), Fps);

		Assert.IsTrue(result.Frames[0].HasGpsFix);
		Assert.AreEqual(50.061, result.Frames[0].Latitude, 1e-9);
		Assert.AreEqual(19.938, result.Frames[0].Longitude, 1e-9);
	}

	[TestMethod]
	public void Parse_DegreesLabelledAsRadians_AreNotAPlace()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(DjmdSample.Stream(
			new DjmdSample { Lat = 50.1, Lon = 19.9 },
			new DjmdSample { CoordUnit = 0, Lat = 50.1, Lon = 19.9 }), Fps);

		Assert.IsFalse(result.Frames[1].HasGpsFix, "50 rad is 2865 degrees - not a latitude");
	}

	[TestMethod]
	public void Parse_UnknownCoordinateUnit_IsNoFix()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(DjmdSample.Stream(new DjmdSample { CoordUnit = 7 }), Fps);

		Assert.IsFalse(result.Frames[0].HasGpsFix);
	}

	[TestMethod]
	public void Parse_ReadsOrientationAndDeviceClock()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(DjmdSample.Stream(
			new DjmdSample { Quaternion = (1f, 0f, 0f, 0f), DeviceTimeMicroseconds = 5_020_001 }), Fps);

		Assert.AreEqual(new TelemetryOrientation(1, 0, 0, 0), result.Frames[0].Orientation);
		Assert.AreEqual(5_020_001L, result.Frames[0].DeviceTimeMicroseconds);
	}

	[TestMethod]
	public void Parse_MissingOrInvalidOrientation_IsNull()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(DjmdSample.Stream(
			new DjmdSample { Quaternion = null, DeviceTimeMicroseconds = null },
			new DjmdSample { Quaternion = (0f, 0f, 0f, 0f) },
			new DjmdSample { Quaternion = (float.NaN, 0f, 0f, 1f) },
			new DjmdSample { Quaternion = (2f, 0f, 0f, 0f) }), Fps);

		Assert.IsTrue(result.Frames.All(f => f.Orientation is null));
		Assert.IsNull(result.Frames[0].DeviceTimeMicroseconds);
		Assert.IsTrue(result.Frames.Skip(1).All(f => f.HasGpsFix), "a bad quaternion does not touch the rest of the sample");
	}

	[TestMethod]
	public void Parse_ZeroZeroWithFixFlag_IsTreatedAsNoFix()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(DjmdSample.Stream(
			new DjmdSample { Lat = 50.1, Lon = 19.9 },
			new DjmdSample { Lat = 0, Lon = 0 }), Fps);

		Assert.IsFalse(result.Frames[1].HasGpsFix);
		Assert.AreEqual(50.1, result.Frames[1].Latitude);
	}

	[TestMethod]
	public void Parse_IgnoresUnknownFields()
	{
		Proto sample = new DjmdSample().ToProto()
			.Varint(9, 12345)
			.Message(99, new Proto().String(1, "future firmware field"))
			.Float(50, 1.5f);
		byte[] stream = new Proto().Varint(1, 1).Message(3, sample).Double(7, 2.5).ToArray();

		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(stream, Fps);

		Assert.AreEqual(1, result.Frames.Count);
		Assert.AreEqual(50.061, result.Frames[0].Latitude);
	}

	[TestMethod]
	public void Parse_TruncatedLastSample_KeepsTheCompleteOnes()
	{
		byte[] stream = DjmdSample.Stream(new DjmdSample(), new DjmdSample { Lat = 51 });

		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(stream.AsMemory(0, stream.Length - 5), Fps);

		Assert.AreEqual(1, result.Frames.Count);
	}

	[TestMethod]
	public void Parse_MissingOptionalParts_LeavesThemNull()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(
			DjmdSample.Stream(new DjmdSample { Timestamp = null, DeviceName = null }), Fps);

		Assert.IsNull(result.Frames[0].GpsTimestamp);
		Assert.IsNull(result.CameraModel);
	}

	[TestMethod]
	public void Parse_ZeroShutterDenominator_IsIgnoredNotDividedBy()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(
			DjmdSample.Stream(new DjmdSample { Shutter = (1, 0) }), Fps);

		Assert.IsNull(result.Frames[0].ShutterSeconds);
	}

	[TestMethod]
	public void Parse_NoSamples_Throws()
	{
		Assert.ThrowsExactly<InvalidOperationException>(() => DjiMetaTelemetryParser.Parse(new Proto().Varint(1, 1).ToArray(), Fps));
	}
}
