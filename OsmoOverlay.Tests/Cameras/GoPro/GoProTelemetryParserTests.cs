using OsmoOverlay.Cameras.GoPro;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Cameras.GoPro;

[TestClass]
public sealed class GoProTelemetryParserTests
{
	private const double G = 9.80665;
	private const double Fps = 25;

	private static GpmfStream Stream(string key, int axes, double[] values, ulong? stamp = null, string? orientation = null, string? utc = null,
		int? fix = null, double? precision = null)
	{
		return new GpmfStream(key, axes, values, stamp, orientation, utc, fix, precision);
	}

	private static GpmfPacket Packet(double start, params GpmfStream[] streams)
	{
		return new GpmfPacket(start, 1, [new GpmfDevice("HERO12 Black", streams)]);
	}

	private static double[] LevelAccelerometer()
	{
		return [.. Enumerable.Range(0, 200).SelectMany(_ => new[] { G, 0, 0 })];
	}

	private static double[] Ramp()
	{
		return [.. Enumerable.Range(0, 200).SelectMany(i => new[] { G * i / 200, 0, 0 })];
	}

	/// <summary>A second of a camera lying level: ACCL at 200 Hz reads (up 1 g, 0, 0) in ZXY, GRAV one a frame.</summary>
	private static GpmfPacket LevelSecond(double start, ulong stamp, params GpmfStream[] more)
	{
		return Packet(start, [
			Stream("ACCL", 3, LevelAccelerometer(), stamp + 1000, "ZXY"),
			Stream("GRAV", 3, [.. Enumerable.Range(0, 25).SelectMany(_ => new[] { 0.0, 1.0, 0.0 })], stamp),
			Stream("SHUT", 1, [.. Enumerable.Repeat(0.002, 25)], stamp),
			Stream("ISOE", 1, [.. Enumerable.Repeat(251.0, 25)], stamp),
			Stream("WBAL", 1, [.. Enumerable.Repeat(4000.0, 9)], stamp),
			.. more
		]);
	}

	[TestMethod]
	public void Parse_DiagnosticStreamsWithoutSensorTimestampsUsePacketTime()
	{
		List<TelemetryFrame> frames = GoProTelemetryParser.Parse([
			LevelSecond(0, 1_000_000),
			LevelSecond(1, 2_000_000, Stream("AALP", 2, [-45, -30]), Stream("WNDM", 2, [0, 3]), Stream("YAVG", 1, [123]))
		], Fps, 50).Frames;
		Assert.IsNull(frames[24].AudioRmsDbfs);
		Assert.AreEqual(-45, frames[25].AudioRmsDbfs);
		Assert.AreEqual(-30, frames[25].AudioPeakDbfs);
		Assert.AreEqual(false, frames[25].WindProcessingActive);
		Assert.AreEqual(3, frames[25].WindProcessingLevel);
		Assert.AreEqual(123, frames[25].ImageLuma);
	}

	[TestMethod]
	public void Parse_DiagnosticsReadRecordedValuesAtTheirSensorTimesWithoutUsingFutureSamples()
	{
		GpmfPacket packet = LevelSecond(0, 1_000_000,
			Stream("AALP", 2, [-43, -33, -49, -41], 1_200_000),
			Stream("WNDM", 2, [1, 35, 0, 2], 1_200_000),
			Stream("YAVG", 1, [98, 111], 1_000_000));
		List<TelemetryFrame> frames = GoProTelemetryParser.Parse([packet], Fps, 25).Frames;
		Assert.IsNull(frames[4].AudioRmsDbfs, "the first audio sample has not arrived yet");
		Assert.IsNull(frames[4].WindProcessingActive);
		Assert.AreEqual(-43, frames[5].AudioRmsDbfs);
		Assert.AreEqual(-33, frames[5].AudioPeakDbfs);
		Assert.AreEqual(true, frames[5].WindProcessingActive);
		Assert.AreEqual(35, frames[5].WindProcessingLevel);
		Assert.AreEqual(98, frames[0].ImageLuma);
		Assert.AreEqual(111, frames[20].ImageLuma);
		Assert.AreEqual(-49, frames[20].AudioRmsDbfs);
		Assert.AreEqual(false, frames[20].WindProcessingActive);
		Assert.AreEqual(2, frames[20].WindProcessingLevel);
	}

	[TestMethod]
	public void Parse_DiagnosticGapsAndMissingStreamsStayUnknown()
	{
		List<TelemetryFrame> frames = GoProTelemetryParser.Parse([
			LevelSecond(0, 1_000_000, Stream("AALP", 2, [-40, -30], 1_000_000), Stream("YAVG", 1, [100], 1_000_000)),
			LevelSecond(1, 2_000_000),
			LevelSecond(2, 3_000_000, Stream("AALP", 2, [-50, -20], 3_000_000))
		], Fps, 100).Frames;
		Assert.AreEqual(-40, frames[0].AudioRmsDbfs);
		Assert.IsNull(frames[25].AudioRmsDbfs);
		Assert.IsNull(frames[25].ImageLuma);
		Assert.AreEqual(-50, frames[50].AudioRmsDbfs);
		Assert.IsNull(frames[75].AudioRmsDbfs, "do not extend the last block indefinitely");
		Assert.IsTrue(frames.All(f => f.WindProcessingActive is null && f.WindProcessingLevel is null));
	}

	[TestMethod]
	public void Parse_DiagnosticSamplesUseTheNextBlockClockWhenItsDurationDiffersFromTheContainer()
	{
		List<TelemetryFrame> frames = GoProTelemetryParser.Parse([
			LevelSecond(0, 1_000_000, Stream("AALP", 2, [-10, -5, -20, -15], 1_000_000)),
			LevelSecond(1, 2_000_000, Stream("AALP", 2, [-30, -25], 1_800_000))
		], Fps, 50).Frames;
		Assert.AreEqual(-20, frames[10].AudioRmsDbfs);
		Assert.AreEqual(-30, frames[20].AudioRmsDbfs);
	}

	[TestMethod]
	public void Parse_DiagnosticsRejectInvalidRangesAndUnexpectedChannelLayouts()
	{
		List<TelemetryFrame> frames = GoProTelemetryParser.Parse([
			LevelSecond(0, 1_000_000,
				Stream("AALP", 2, [double.NaN, 2, -128, 0], 1_000_000),
				Stream("WNDM", 2, [2, -1, 0, 100], 1_000_000),
				Stream("YAVG", 1, [256, 0], 1_000_000))
		], Fps, 25).Frames;
		Assert.IsNull(frames[0].AudioRmsDbfs);
		Assert.IsNull(frames[0].AudioPeakDbfs);
		Assert.IsNull(frames[0].WindProcessingActive);
		Assert.IsNull(frames[0].WindProcessingLevel);
		Assert.IsNull(frames[0].ImageLuma);
		Assert.AreEqual(-128, frames[20].AudioRmsDbfs);
		Assert.AreEqual(0, frames[20].AudioPeakDbfs);
		Assert.AreEqual(false, frames[20].WindProcessingActive);
		Assert.AreEqual(100, frames[20].WindProcessingLevel);
		Assert.AreEqual(0, frames[20].ImageLuma);
		List<TelemetryFrame> wrongAxes = GoProTelemetryParser.Parse([LevelSecond(0, 1_000_000, Stream("AALP", 1, [-40]), Stream("YAVG", 2, [100, 200]))], Fps, 25).Frames;
		Assert.IsTrue(wrongAxes.All(f => f.AudioRmsDbfs is null && f.ImageLuma is null));
	}

	[TestMethod]
	public void Parse_ImuTemperatureFollowsItsSensorBlockAndKeepsMissingReadingsUnknown()
	{
		GpmfStream accel = Stream("ACCL", 3, LevelAccelerometer(), 1_000_000, "ZXY") with { TemperatureCelsius = -3.5 };
		TelemetryExtractionResult result = GoProTelemetryParser.Parse(
		[
			Packet(0, accel), Packet(1, accel with { StampMicroseconds = 2_000_000, TemperatureCelsius = 20 }),
			Packet(2, accel with { StampMicroseconds = 3_000_000, TemperatureCelsius = null })
		], Fps, 75);
		Assert.AreEqual(-3.5, result.Frames[0].ImuTemperatureCelsius);
		Assert.AreEqual(20, result.Frames[25].ImuTemperatureCelsius);
		Assert.IsNull(result.Frames[50].ImuTemperatureCelsius);
		Assert.IsTrue(result.Frames.All(f => f.AmbientTemperatureCelsius is null));
	}

	[TestMethod]
	public void Parse_OneFramePerVideoFrameWithTheCamerasAxesInG()
	{
		TelemetryExtractionResult result = GoProTelemetryParser.Parse([LevelSecond(0, 1_000_000), LevelSecond(1, 2_000_000)], Fps, 50);

		Assert.AreEqual(50, result.Frames.Count);
		Assert.AreEqual("HERO12 Black", result.CameraModel);
		TelemetryFrame frame = result.Frames[30];
		Assert.AreEqual(30, frame.FrameNumber);
		Assert.AreEqual(30 / Fps, frame.SampleTimeSeconds, 1e-12);
		Assert.AreEqual(0, frame.AccelX, 1e-9);
		Assert.AreEqual(0, frame.AccelY, 1e-9);
		Assert.AreEqual(1, frame.AccelZ, 1e-9, "what the sensor feels lying level is gravity's reaction, up");
		Assert.AreEqual(251f, frame.Iso);
		Assert.AreEqual(0.002, frame.ShutterSeconds!.Value, 1e-12);
		Assert.AreEqual(4000, frame.ColorTemperatureKelvin);
	}

	[TestMethod]
	public void Parse_TheAccelerometerIsAveragedOverTheFrame()
	{
		double[] samples = [.. Enumerable.Range(0, 200).SelectMany(i => new[] { i % 2 == 0 ? 2 * G : 0, 0, 0 })];
		GpmfPacket packet = Packet(0, Stream("ACCL", 3, samples, 1_000_000, "ZXY"), Stream("SHUT", 1, [.. Enumerable.Repeat(0.002, 25)], 1_000_000));

		TelemetryExtractionResult result = GoProTelemetryParser.Parse([packet], Fps, 25);

		Assert.AreEqual(1, result.Frames[10].AccelZ, 0.12, "the mean of 8 or 9 samples, not one of them");
	}

	[TestMethod]
	public void Parse_SamplesAreOnTheSensorsClockFromTheFirstFrame()
	{
		GpmfPacket first = Packet(0, Stream("ACCL", 3, Ramp(), 1_000_000 + 40_000, "ZXY"), Stream("SHUT", 1, [.. Enumerable.Repeat(0.002, 25)], 1_000_000));
		GpmfPacket second = Packet(1, Stream("ACCL", 3, Ramp(), 2_000_000 + 40_000, "ZXY"), Stream("SHUT", 1, [.. Enumerable.Repeat(0.002, 25)], 2_000_000));

		TelemetryExtractionResult result = GoProTelemetryParser.Parse([first, second], Fps, 50);

		// The ramp starts a frame in (40 ms) on the sensors' clock: frame 1 is its start, 13 frames later half a ramp (0.52 s), and the second block starts at frame 26.
		Assert.AreEqual(0.0, result.Frames[1].AccelZ, 0.02);
		Assert.AreEqual(0.52, result.Frames[14].AccelZ, 0.03);
		Assert.AreEqual(0.04, result.Frames[27].AccelZ, 0.03);
	}

	[TestMethod]
	public void Parse_WithoutTheSensorsClockThePacketsTimeStandsIn()
	{
		TelemetryExtractionResult result = GoProTelemetryParser.Parse([Packet(0, Stream("ACCL", 3, Ramp())), Packet(1, Stream("ACCL", 3, Ramp()))], Fps, 50);

		Assert.AreEqual(0.0, result.Frames[0].AccelZ, 0.02);
		Assert.AreEqual(0.5, result.Frames[12].AccelZ, 0.03);
		Assert.AreEqual(0.04, result.Frames[26].AccelZ, 0.03);
	}

	[TestMethod]
	public void Parse_AFrameBeyondTheLastSampleHoldsIt()
	{
		TelemetryExtractionResult result = GoProTelemetryParser.Parse([LevelSecond(0, 1_000_000)], Fps, 30);

		Assert.AreEqual(1, result.Frames[29].AccelZ, 1e-9);
		Assert.AreEqual(251f, result.Frames[29].Iso);
	}

	[TestMethod]
	public void Parse_ACameraWithoutAGpsHasNoFixAndNoPosition()
	{
		TelemetryExtractionResult result = GoProTelemetryParser.Parse([LevelSecond(0, 1_000_000)], Fps, 25);

		Assert.IsTrue(result.Frames.All(f => !f.HasGpsFix && f.Latitude == 0 && f.Longitude == 0 && f.GpsTimestamp is null && f.GpsSpeedMs is null));
	}

	[TestMethod]
	public void Parse_Gps9GivesPositionSpeedAndWholeSecondsOfTime()
	{
		double[] rows =
		[
			53.154664, 18.900202, 87.4, 1.5, 1.6, 9_800, 51_209.2, 1.2, 3,
			53.154674, 18.900222, 87.4, 1.6, 1.7, 9_800, 51_209.7, 1.2, 3
		];
		TelemetryExtractionResult result = GoProTelemetryParser.Parse([LevelSecond(0, 1_000_000, Stream("GPS9", 9, rows, 1_000_000))], Fps, 25);

		TelemetryFrame first = result.Frames[0];
		Assert.IsTrue(first.HasGpsFix);
		Assert.AreEqual(53.154664, first.Latitude, 1e-9);
		Assert.AreEqual(87.4, first.AltitudeMeters, 1e-9);
		Assert.AreEqual(1.5, first.GpsSpeedMs!.Value, 1e-9);
		Assert.AreEqual(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(9_800).AddSeconds(51_209), first.GpsTimestamp, "whole seconds of 9800 days and 51209.2 s into 2000");
	}

	[TestMethod]
	public void Parse_ASampleWithoutAFixOrWithPoorPrecisionHoldsTheLastPosition()
	{
		double[] rows =
		[
			53.1, 18.9, 87.0, 1, 1, 9_800, 100.0, 1.2, 3,
			0.0, 0.0, 0.0, 0, 0, 9_800, 100.5, 99.99, 0,
			54.0, 19.0, 90.0, 1, 1, 9_800, 101.0, 25.0, 3
		];
		GpmfPacket packet = Packet(0, Stream("ACCL", 3, LevelAccelerometer(), 1_000_000, "ZXY"), Stream("GPS9", 9, rows, 1_000_000));

		TelemetryExtractionResult result = GoProTelemetryParser.Parse([packet], 3, 3);

		Assert.IsTrue(result.Frames[0].HasGpsFix);
		Assert.IsFalse(result.Frames[1].HasGpsFix, "no lock");
		Assert.AreEqual(53.1, result.Frames[1].Latitude, 1e-9, "held, not snapped to (0, 0)");
		Assert.IsFalse(result.Frames[2].HasGpsFix, "a dilution of 25 is no position");
		Assert.AreEqual(53.1, result.Frames[2].Latitude, 1e-9);
	}

	[TestMethod]
	public void Parse_Gps5TakesFixPrecisionAndTimeFromItsBlock()
	{
		double[] rows = [53.1, 18.9, 87.0, 2.0, 2.1, 53.2, 18.8, 88.0, 3.0, 3.1];
		GpmfStream gps = Stream("GPS5", 5, rows, 1_000_000, utc: "261021142049.250", fix: 3, precision: 1.5);
		GpmfStream noFix = Stream("GPS5", 5, rows, 2_000_000, utc: "261021142050.250", fix: 0, precision: 99.99);

		TelemetryExtractionResult result = GoProTelemetryParser.Parse([LevelSecond(0, 1_000_000, gps), LevelSecond(1, 2_000_000, noFix)], Fps, 50);

		Assert.IsTrue(result.Frames[0].HasGpsFix);
		Assert.AreEqual(53.1, result.Frames[0].Latitude, 1e-9);
		Assert.AreEqual(2.0, result.Frames[0].GpsSpeedMs!.Value, 1e-9);
		Assert.AreEqual(new DateTime(2026, 10, 21, 14, 20, 49, DateTimeKind.Utc), result.Frames[0].GpsTimestamp);
		Assert.IsFalse(result.Frames[30].HasGpsFix);
	}

	[TestMethod]
	public void Parse_AGpsThatStoppedReportingLeavesTheFramesWithoutAFix()
	{
		GpmfStream gps = Stream("GPS5", 5, [53.1, 18.9, 87.0, 2.0, 2.1], 1_000_000, fix: 3, precision: 1);
		var packets = new List<GpmfPacket> { LevelSecond(0, 1_000_000, gps) };
		for (int s = 1; s < 6; s++) packets.Add(LevelSecond(s, 1_000_000UL + (ulong)s * 1_000_000));

		TelemetryExtractionResult result = GoProTelemetryParser.Parse(packets, Fps, 150);

		Assert.IsTrue(result.Frames[0].HasGpsFix);
		Assert.IsFalse(result.Frames[149].HasGpsFix);
	}

	[TestMethod]
	public void Parse_TheCamerasGravityBecomesTheOrientation()
	{
		double[] gravity = [0.1, 0.9, 0.42, 0, 0.5, 0.866, 0.2, -0.9, 0.38, 0, -1, 0];
		GpmfPacket packet = Packet(0, Stream("ACCL", 3, LevelAccelerometer(), 1_000_000, "ZXY"), Stream("GRAV", 3, gravity, 1_000_000));

		TelemetryExtractionResult result = GoProTelemetryParser.Parse([packet], 4, 4);

		for (int i = 0; i < 4; i++)
		{
			double[] g = gravity[(i * 3)..(i * 3 + 3)];
			double length = Math.Sqrt(g.Sum(v => v * v));
			Direction back = GoProTilt.Gravity(result.Frames[i].Orientation!.Value);
			Assert.AreEqual(g[0] / length, back.X, 1e-6, $"frame {i} x");
			Assert.AreEqual(g[1] / length, back.Y, 1e-6, $"frame {i} y");
			Assert.AreEqual(g[2] / length, back.Z, 1e-6, $"frame {i} z");
		}
	}

	[TestMethod]
	public void Parse_WithoutAGravityVectorThereIsNoOrientation()
	{
		GpmfPacket packet = Packet(0, Stream("ACCL", 3, LevelAccelerometer(), 1_000_000, "ZXY"));

		Assert.IsTrue(GoProTelemetryParser.Parse([packet], Fps, 25).Frames.All(f => f.Orientation is null));
	}

	[TestMethod]
	public void Parse_WithoutAnAccelerometerIsAnError()
	{
		Assert.ThrowsExactly<InvalidDataException>(() => GoProTelemetryParser.Parse([Packet(0, Stream("GRAV", 3, [0, 1, 0]))], Fps, 25));
		Assert.ThrowsExactly<InvalidDataException>(() => GoProTelemetryParser.Parse([], Fps, 25));
	}

	[TestMethod]
	public void Parse_AnAccelerometerOfTheWrongWidthIsAnError()
	{
		Assert.ThrowsExactly<InvalidDataException>(() => GoProTelemetryParser.Parse([Packet(0, Stream("ACCL", 1, [1, 2, 3, 4], 1_000_000, "ZXY"))], Fps, 25));
	}

	[TestMethod]
	public void ImuAxes_FollowTheLettersOfTheInputOrientation()
	{
		CollectionAssert.AreEqual(new[] { (2, 1.0), (0, 1.0), (1, 1.0) }, GoProTelemetryParser.ImuAxes("ZXY"));
		CollectionAssert.AreEqual(new[] { (1, 1.0), (0, -1.0), (2, 1.0) }, GoProTelemetryParser.ImuAxes("YxZ"));
	}

	[TestMethod]
	public void ImuAxes_AnythingElseIsTheHero12s()
	{
		(int, double)[] hero12 = GoProTelemetryParser.ImuAxes("ZXY");

		CollectionAssert.AreEqual(hero12, GoProTelemetryParser.ImuAxes(null));
		CollectionAssert.AreEqual(hero12, GoProTelemetryParser.ImuAxes("ZZY"));
		CollectionAssert.AreEqual(hero12, GoProTelemetryParser.ImuAxes("AB"));
		CollectionAssert.AreEqual(hero12, GoProTelemetryParser.ImuAxes("QXY"));
	}

	[TestMethod]
	public void Durations_FromTheTimeToSampleTable()
	{
		double[] durations = GoProTrack.Durations([(2, 90_000), (1, 45_000)], 4, 90_000);

		CollectionAssert.AreEqual(new[] { 1.0, 1.0, 0.5, 0.0 }, durations);
	}
}
