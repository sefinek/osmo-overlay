using System.Text;
using System.Text.Json.Nodes;
using OsmoOverlay.Core.Telemetry.External;
using OsmoOverlay.Dji.Drones;
using static OsmoOverlay.Tests.Dji.Drones.FlightLogBuilder;

namespace OsmoOverlay.Tests.Dji.Drones;

[TestClass]
public sealed class FlightLogTests
{
	private static readonly DateTime Started = new(2026, 10, 29, 13, 0, 0, DateTimeKind.Utc);

	[TestMethod]
	public void Crc64_IsTheJonesVariantWithTheReferenceCheckValue()
	{
		Assert.AreEqual(0xE9C6D914C4B8D9CAUL, FlightLogCrypto.Crc64(0, "123456789"u8));
	}

	[TestMethod]
	public void XorKey_MatchesTheKeysOfARealLog()
	{
		CollectionAssert.AreEqual(Convert.FromHexString("7ba06bffb1f09682"), FlightLogCrypto.XorKey(149, 0));
		CollectionAssert.AreEqual(Convert.FromHexString("66d5c06bdde09e49"), FlightLogCrypto.XorKey(7, 1));
		CollectionAssert.AreEqual(Convert.FromHexString("84790a13315938f2"), FlightLogCrypto.XorKey(200, 25));
	}

	[TestMethod]
	public void Parse_ReadsVersionAndStartFromTheEncryptedDetails()
	{
		FlightLog log = FlightLog.Parse(new FlightLogBuilder(14, Started).Build());

		Assert.AreEqual(14, log.Version);
		Assert.IsTrue(log.IsEncrypted);
		Assert.AreEqual(Started, log.StartedUtc);
		Assert.AreEqual(4, log.KeychainVersion);
		Assert.AreEqual(3, log.Department);
	}

	[TestMethod]
	public void Parse_ReadsTheStartOfAnUnencryptedLog()
	{
		FlightLog log = FlightLog.Parse(new FlightLogBuilder(12, Started).Build());

		Assert.IsFalse(log.IsEncrypted);
		Assert.AreEqual(Started, log.StartedUtc);
	}

	[TestMethod]
	public void Parse_RejectsAFileThatIsNotALog()
	{
		Assert.ThrowsExactly<InvalidDataException>(() => FlightLog.Parse(new byte[500]));
		Assert.ThrowsExactly<InvalidDataException>(() => FlightLog.Parse(Encoding.ASCII.GetBytes(new string('x', 500))));
	}

	[TestMethod]
	public void BuildKeychainsRequest_GroupsTheKeyBlocksBySession()
	{
		byte[] first = [1, 2, 3, 4];
		byte[] second = [9, 8, 7];
		var builder = new FlightLogBuilder(14, Started)
			.AddKeyBlock(FlightLogFeaturePoints.Base, first)
			.AddKeyBlock(FlightLogFeaturePoints.Camera, second)
			.Add(Osd, OsdRecord(50, 20, 1, 0, 0, 10, true, 90))
			.NewSession()
			.AddKeyBlock(FlightLogFeaturePoints.Base, second);

		KeychainsRequest request = FlightLog.Parse(builder.Build()).BuildKeychainsRequest();

		Assert.AreEqual(2, request.Keychains.Count);
		Assert.AreEqual(2, request.Keychains[0].Count);
		Assert.AreEqual(FlightLogFeaturePoints.Camera, request.Keychains[0][1].FeaturePoint);
		CollectionAssert.AreEqual(first, request.Keychains[0][0].Data);
		CollectionAssert.AreEqual(second, request.Keychains[1].Single().Data);
	}

	[TestMethod]
	public void Decode_ReadsEncryptedRecordsWithTheIvChainedPerFeaturePoint()
	{
		var builder = new FlightLogBuilder(14, Started);
		builder.Add(Home, HomeRecord(120.5));
		for (int i = 0; i < 4; i++)
		{
			builder.Add(Battery, BatteryRecord(80 - i));
			builder.Add(Osd, OsdRecord(50.25 + i * 0.001, 20.5, 10 + i, 3, 4, 12, true));
		}

		FlightLogData data = FlightLog.Parse(builder.Build()).Decode([new Keychain(builder.Keys)]);

		Assert.AreEqual(4, data.Samples.Count);
		FlightSample last = data.Samples[3];
		Assert.AreEqual(0.3, last.Seconds, 1e-9);
		Assert.AreEqual(50.253, last.Latitude, 1e-9);
		Assert.AreEqual(20.5, last.Longitude, 1e-9);
		Assert.AreEqual(13, last.HeightMeters, 1e-9);
		Assert.AreEqual(133.5, last.AltitudeMeters, 1e-6);
		Assert.AreEqual(5, last.SpeedMs, 1e-9);
		Assert.AreEqual(12, last.Satellites);
		Assert.IsTrue(last.GpsValid);
		Assert.AreEqual(77, data.Batteries!.Last().Battery.Percent);
		Assert.AreEqual(11, data.Batteries!.Last().Battery.VoltageV!.Value, 1e-9);
	}

	[TestMethod]
	public void Decode_TheClimbRateIsTheHeightOverASecondAroundTheSample()
	{
		var builder = new FlightLogBuilder(12, Started);
		for (int i = 0; i < 40; i++) builder.Add(Osd, OsdRecord(50, 20, 0.1 * i * 2, 0, 0, 10, true));

		IReadOnlyList<FlightSample> samples = FlightLog.Parse(builder.Build()).Decode(null).Samples;

		Assert.AreEqual(2, samples[20].VerticalSpeedMs!.Value, 1e-9);
		Assert.AreEqual(2, samples[0].VerticalSpeedMs!.Value, 1e-9, "the window is cut short at the ends");
		Assert.AreEqual(2, samples[39].VerticalSpeedMs!.Value, 1e-9);
	}

	[TestMethod]
	public void Decode_ARecordWhoseFeaturePointHasNoKeyIsLeftOut()
	{
		var builder = new FlightLogBuilder(14, Started);
		builder.Add(Osd, OsdRecord(50, 20, 1, 0, 0, 10, true));
		var withoutBase = new Keychain(builder.Keys.Where(k => k.Key != FlightLogFeaturePoints.Base).ToDictionary());

		Assert.AreEqual(0, FlightLog.Parse(builder.Build()).Decode([withoutBase]).Samples.Count);
	}

	[TestMethod]
	public void Decode_WithWrongKeysFindsNoSamples()
	{
		var builder = new FlightLogBuilder(14, Started);
		builder.Add(Osd, OsdRecord(50, 20, 1, 0, 0, 10, true, 90));
		var wrong = new Keychain(new Dictionary<int, FeatureKey> { [FlightLogFeaturePoints.Base] = new(new byte[32], new byte[16]) });

		Assert.AreEqual(0, FlightLog.Parse(builder.Build()).Decode([wrong]).Samples.Count);
	}

	[TestMethod]
	public void Decode_AnEncryptedLogNeedsItsKeychains()
	{
		FlightLog log = FlightLog.Parse(new FlightLogBuilder(14, Started).Add(Osd, OsdRecord(50, 20, 1, 0, 0, 10, true, 90)).Build());

		Assert.ThrowsExactly<InvalidOperationException>(() => log.Decode(null));
	}

	[TestMethod]
	public void Decode_TakesTheRecordingSpansFromTheCameraRecords()
	{
		var builder = new FlightLogBuilder(12, Started);
		for (int i = 0; i < 30; i++)
		{
			if (i == 5) builder.Add(Camera, CameraRecord(true));
			if (i == 15) builder.Add(Camera, CameraRecord(false));
			if (i == 20) builder.Add(Camera, CameraRecord(true));
			builder.Add(Osd, OsdRecord(50, 20, 1, 0, 0, 10, true, 90));
		}

		FlightLogData data = FlightLog.Parse(builder.Build()).Decode(null);

		Assert.AreEqual(2, data.Recordings.Count);
		Assert.AreEqual(0.4, data.Recordings[0].StartSeconds, 1e-9);
		Assert.AreEqual(1.4, data.Recordings[0].EndSeconds, 1e-9);
		Assert.AreEqual(1.9, data.Recordings[1].StartSeconds, 1e-9);
		Assert.AreEqual(2.9, data.Recordings[1].EndSeconds, 1e-9, "a recording still going at the end lasts to the last sample");
	}

	[TestMethod]
	public void Decode_ClockIsTheMiddleOfWhatTheTimeStampsSay()
	{
		var builder = new FlightLogBuilder(12, Started);
		for (int i = 0; i < 40; i++)
		{
			builder.Add(Osd, OsdRecord(50, 20, 1, 0, 0, 10, true, 90));
			if (i % 10 == 9) builder.Add(Custom, CustomRecord(Started.AddSeconds(i * 0.1 + (i == 19 ? 30 : 0))));
		}

		FlightLogData data = FlightLog.Parse(builder.Build()).Decode(null);

		Assert.AreEqual(Started, data.ClockUtc!.Value);
	}

	[TestMethod]
	public void Decode_SkipsDamagedRecordsAndKeepsTheOnesAfterThem()
	{
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Osd, OsdRecord(50, 20, 1, 0, 0, 10, true, 90));
		byte[] good = builder.Build();
		byte[] withJunk = [.. good, 0x11, 0x22, 0x33, 0xFF];
		var second = new FlightLogBuilder(12, Started);
		second.Add(Osd, OsdRecord(51, 20, 1, 0, 0, 10, true, 90));
		byte[] tail = second.Build()[(100 + 436)..];

		FlightLogData data = FlightLog.Parse([.. withJunk, .. tail]).Decode(null);

		Assert.AreEqual(2, data.Samples.Count);
		Assert.AreEqual(51, data.Samples[1].Latitude, 1e-9);
	}

	[TestMethod]
	public void ToTrack_LeavesOutSamplesWithoutAFix()
	{
		var builder = new FlightLogBuilder(12, Started);
		builder.Add(Custom, CustomRecord(Started));
		builder.Add(Osd, OsdRecord(0, 0, 0, 0, 0, 0, false, 90));
		for (int i = 0; i < 5; i++)
		{
			builder.Add(Battery, BatteryRecord(90 - i));
			builder.Add(Osd, OsdRecord(50 + i * 0.0001, 20, 5, 2, 0, 11, true));
		}

		FlightLog log = FlightLog.Parse(builder.Build());

		ExternalTrack track = DjiFlightLogFormat.ToTrack(log, log.Decode(null));

		Assert.AreEqual(5, track.Points.Count);
		Assert.AreEqual(2, track.Points[0].SpeedMs!.Value, 1e-9);
		Assert.AreEqual(5, track.DroneBatteries.Count);
		Assert.AreEqual(0, track.Batteries.Count, "the device battery widget is for FIT, the drone has its own");
	}

	[TestMethod]
	public void Format_IsReadThroughTheTrackReaderOnceRegistered()
	{
		ExternalTrackReader.Register(new DjiFlightLogFormat());
		var builder = new FlightLogBuilder(12, Started);
		for (int i = 0; i < 20; i++) builder.Add(Osd, OsdRecord(50 + i * 0.0001, 20, 5, 2, 0, 11, true, 90));
		string path = Path.Combine(Path.GetTempPath(), $"FlightRecord_{Guid.NewGuid():N}.txt");
		File.WriteAllBytes(path, builder.Build());
		try
		{
			Assert.IsTrue(ExternalTrackReader.IsTrackFile(path));
			Assert.AreEqual(20, ExternalTrackReader.Read(path).Points.Count);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[TestMethod]
	public void Format_DoesNotClaimOtherTextFiles()
	{
		var format = new DjiFlightLogFormat();

		Assert.IsFalse(format.Recognizes("notes.txt", Encoding.ASCII.GetBytes("just some notes, long enough to look at")));
		Assert.IsFalse(format.Recognizes("track.gpx", new FlightLogBuilder(12, Started).Build().AsSpan(0, 128)));
		Assert.IsTrue(format.Recognizes("FlightRecord.TXT", new FlightLogBuilder(12, Started).Build().AsSpan(0, 128)));
	}

	[TestMethod]
	public void RequestJson_NamesTheFeaturePointsAndCarriesBase64()
	{
		JsonObject json = DjiKeychains.RequestJson(new KeychainsRequest(4, 3, [[(FlightLogFeaturePoints.Base, [1, 2, 3])], [(FlightLogFeaturePoints.Camera, [4])]]));

		Assert.AreEqual(4, (int)json["version"]!);
		Assert.AreEqual(3, (int)json["department"]!);
		JsonArray sessions = json["keychainsArray"]!.AsArray();
		Assert.AreEqual(2, sessions.Count);
		Assert.AreEqual("FR_Standardization_Feature_Base_1", (string)sessions[0]![0]!["featurePoint"]!);
		Assert.AreEqual("AQID", (string)sessions[0]![0]!["aesCiphertext"]!);
		Assert.AreEqual("FR_Standardization_Feature_Camera_12", (string)sessions[1]![0]!["featurePoint"]!);
	}

	[TestMethod]
	public void ParseKeychains_ReadsKeysAndIvsPerFeaturePoint()
	{
		JsonNode data = JsonNode.Parse("""
		                               [[{"featurePoint":"FR_Standardization_Feature_Base_1","aesKey":"AQID","aesIv":"BAUG"},
		                                 {"featurePoint":"FR_Standardization_Feature_Unknown_99","aesKey":"AQID","aesIv":"BAUG"}]]
		                               """)!;

		Keychain keychain = DjiKeychains.Parse(data).Single();

		CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, keychain.Keys[FlightLogFeaturePoints.Base].Key);
		CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, keychain.Keys[FlightLogFeaturePoints.Base].Iv);
		Assert.AreEqual(1, keychain.Keys.Count);
	}

	[TestMethod]
	public void FeaturePoints_Version13PutsMoreRecordsUnderTheBaseKey()
	{
		Assert.AreEqual(FlightLogFeaturePoints.Base, FlightLogFeaturePoints.FromRecordType(25, 13));
		Assert.AreEqual(FlightLogFeaturePoints.Camera, FlightLogFeaturePoints.FromRecordType(25, 14));
		Assert.AreEqual(FlightLogFeaturePoints.Plaintext, FlightLogFeaturePoints.FromRecordType(KeyStorage, 14));
	}
}
