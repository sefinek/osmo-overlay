using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Telemetry;

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
			new DjmdSample { FixType = 0, Lat = 0, Lon = 0 }), Fps);

		TelemetryFrame lost = result.Frames[1];
		Assert.IsFalse(lost.HasGpsFix);
		Assert.AreEqual(50.1, lost.Latitude);
		Assert.AreEqual(19.9, lost.Longitude);
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
		var sample = new DjmdSample().ToProto()
			.Varint(9, 12345)
			.Message(99, new Proto().String(1, "future firmware field"))
			.Float(50, 1.5f);
		var stream = new Proto().Varint(1, 1).Message(3, sample).Double(7, 2.5).ToArray();

		TelemetryExtractionResult result = DjiMetaTelemetryParser.Parse(stream, Fps);

		Assert.AreEqual(1, result.Frames.Count);
		Assert.AreEqual(50.061, result.Frames[0].Latitude);
	}

	[TestMethod]
	public void Parse_TruncatedLastSample_KeepsTheCompleteOnes()
	{
		var stream = DjmdSample.Stream(new DjmdSample(), new DjmdSample { Lat = 51 });

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
