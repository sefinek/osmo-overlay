using OsmoOverlay.Cameras.GoPro;
using static OsmoOverlay.Tests.Cameras.GoPro.GpmfBuilder;

namespace OsmoOverlay.Tests.Cameras.GoPro;

[TestClass]
public sealed class GpmfReaderTests
{
	[TestMethod]
	public void Read_DeviceNameAndAScaledStreamWithItsMetadata()
	{
		byte[] packet = Container("DEVC", Text("DVNM", "HERO12 Black"),
			Container("STRM", Longs("STMP", 47_410), Text("ORIN", "ZXY"), Shorts("SCAL", 1, 417), Shorts("ACCL", 3, 417, 0, 834, -417, 0, 0)));

		GpmfDevice device = GpmfReader.Read(packet).Single();

		Assert.AreEqual("HERO12 Black", device.Name);
		GpmfStream accl = device.Streams.Single();
		Assert.AreEqual("ACCL", accl.Key);
		Assert.AreEqual(3, accl.Axes);
		Assert.AreEqual(2, accl.Count);
		Assert.AreEqual(47_410UL, accl.StampMicroseconds);
		Assert.AreEqual("ZXY", accl.InputOrientation);
		CollectionAssert.AreEqual(new[] { 1.0, 0.0, 2.0, -1.0, 0.0, 0.0 }, accl.Values);
	}

	[TestMethod]
	public void Read_ScaleOneForEachAxis()
	{
		byte[] packet = Container("DEVC", Container("STRM", Shorts("SCAL", 1, 10, 100, 1000), Shorts("XYZ_", 3, 10, 10, 10)));

		CollectionAssert.AreEqual(new[] { 1.0, 0.1, 0.01 }, GpmfReader.Read(packet).Single().Streams.Single().Values);
	}

	[TestMethod]
	public void Read_FloatsNeedNoScale()
	{
		byte[] packet = Container("DEVC", Container("STRM", Floats("SHUT", 1, 0.002f, 0.004f)));

		GpmfStream shut = GpmfReader.Read(packet).Single().Streams.Single();

		Assert.AreEqual(2, shut.Count);
		Assert.AreEqual(0.004, shut.Values[1], 1e-9);
	}

	[TestMethod]
	public void Read_AStructureNamedByTheTypeItemBeforeIt()
	{
		byte[] gps9 = Structures("GPS9", "lllllllSS", [531_546_640, 189_002_020, 87_400, 1_500, 1_600, 9_800, 51_200_000, 120, 3]);
		byte[] packet = Container("DEVC", Container("STRM", Text("TYPE", "lllllllSS"),
			Item("SCAL", 'l', 4, 9, ScaleBytes(10_000_000, 10_000_000, 1000, 1000, 100, 1, 1000, 100, 1)), gps9));

		GpmfStream stream = GpmfReader.Read(packet).Single().Streams.Single();

		Assert.AreEqual(9, stream.Axes);
		Assert.AreEqual(53.154664, stream.Values[0], 1e-9);
		Assert.AreEqual(18.900202, stream.Values[1], 1e-9);
		Assert.AreEqual(87.4, stream.Values[2], 1e-9);
		Assert.AreEqual(1.5, stream.Values[3], 1e-9);
		Assert.AreEqual(51_200.0, stream.Values[6], 1e-9);
		Assert.AreEqual(1.2, stream.Values[7], 1e-9);
		Assert.AreEqual(3.0, stream.Values[8], 1e-9);
	}

	[TestMethod]
	public void Read_AStructureOfUnknownTypesIsNoStream()
	{
		byte[] packet = Container("DEVC", Container("STRM", Text("TYPE", "lz"), Structures("GPS9", "ll", [1, 2])));

		Assert.AreEqual(0, GpmfReader.Read(packet).Single().Streams.Count);
	}

	[TestMethod]
	public void Read_TextualStreamsAreNoSamples()
	{
		byte[] packet = Container("DEVC", Container("STRM", Text("STNM", "name"), Text("RMRK", "remark")));

		Assert.AreEqual(0, GpmfReader.Read(packet).Single().Streams.Count);
	}

	[TestMethod]
	public void Read_GpsFixPrecisionAndTimeOfABlock()
	{
		byte[] packet = Container("DEVC", Container("STRM", Text("GPSU", "261021142049.500"), Shorts("GPSF", 1, 3), Shorts("GPSP", 1, 187),
			Shorts("SCAL", 1, 1), Shorts("GPS5", 1, 5)));

		GpmfStream stream = GpmfReader.Read(packet).Single().Streams.Single();

		Assert.AreEqual("261021142049.500", stream.GpsUtc);
		Assert.AreEqual(3, stream.GpsFix);
		Assert.AreEqual(1.87, stream.GpsPrecision!.Value, 1e-9);
	}

	[TestMethod]
	public void Read_SeveralDevicesInOnePacket()
	{
		byte[] packet = [.. Container("DEVC", Text("DVNM", "a")), .. Container("DEVC", Text("DVNM", "b"))];

		CollectionAssert.AreEqual(new[] { "a", "b" }, GpmfReader.Read(packet).Select(d => d.Name).ToArray());
	}

	[TestMethod]
	public void Read_APacketCutShortKeepsTheStreamsThatWereWhole()
	{
		byte[] whole = Container("DEVC", Text("DVNM", "cam"),
			Container("STRM", Shorts("SCAL", 1, 1), Shorts("GRAV", 3, 0, 1, 0)),
			Container("STRM", Shorts("SCAL", 1, 1), Shorts("CORI", 4, 1, 0, 0, 0)));

		GpmfDevice device = GpmfReader.Read(whole[..^6]).Single();

		Assert.AreEqual("cam", device.Name);
		Assert.AreEqual("GRAV", device.Streams.Single().Key);
	}

	[TestMethod]
	public void Read_GarbageIsNoDevices()
	{
		Assert.AreEqual(0, GpmfReader.Read([]).Count);
		Assert.AreEqual(0, GpmfReader.Read([1, 2, 3]).Count);
		Assert.AreEqual(0, GpmfReader.Read(new byte[64]).Count);
	}

	[TestMethod]
	public void ParseGpsUtc_ReadsTheReceiversTime()
	{
		Assert.AreEqual(new DateTime(2026, 10, 21, 14, 20, 49, 500, DateTimeKind.Utc), GpmfReader.ParseGpsUtc("261021142049.500"));
		Assert.AreEqual(new DateTime(2026, 10, 21, 14, 20, 49, DateTimeKind.Utc), GpmfReader.ParseGpsUtc("261021142049"));
		Assert.IsNull(GpmfReader.ParseGpsUtc("tomorrow"));
		Assert.IsNull(GpmfReader.ParseGpsUtc(null));
	}

	private static byte[] ScaleBytes(params int[] values)
	{
		byte[] bytes = new byte[values.Length * 4];
		for (int i = 0; i < values.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(i * 4), values[i]);
		return bytes;
	}
}
