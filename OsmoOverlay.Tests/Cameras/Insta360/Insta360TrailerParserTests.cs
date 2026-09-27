using System.Buffers.Binary;
using System.Text;
using OsmoOverlay.Cameras.Insta360;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Cameras.Insta360;

[TestClass]
public sealed class Insta360TrailerParserTests
{
	private const long FrameMicros = 16_683;

	/// <summary>A file of `mediaBytes` stand-in MP4 data followed by a trailer holding `records` (id, data).</summary>
	private static MemoryStream File(IReadOnlyList<(ushort Id, byte[] Data)> records, bool directory, int mediaBytes = 100)
	{
		var trailer = new MemoryStream();
		List<(ushort Id, int Size, int Offset)> table = [];
		foreach ((var id, var data) in records)
		{
			table.Add((id, data.Length, (int)trailer.Length));
			trailer.Write(data);
			trailer.Write(Footer(id, data.Length));
		}

		byte[] last;
		if (directory)
		{
			var entries = new byte[(table.Count + 1) * 10];
			for (var i = 0; i < table.Count; i++)
			{
				BinaryPrimitives.WriteUInt16LittleEndian(entries.AsSpan((i + 1) * 10), (ushort)(table[i].Id >> 8));
				BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan((i + 1) * 10 + 2), (uint)table[i].Size);
				BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan((i + 1) * 10 + 6), (uint)table[i].Offset);
			}

			trailer.Write(entries);
			last = Footer(0, entries.Length);
		}
		else
		{
			// Sequential: the last record's footer is the one inside the 78-byte end block.
			trailer.SetLength(trailer.Length - 6);
			trailer.Position = trailer.Length;
			last = Footer(records[^1].Id, records[^1].Data.Length);
		}

		var end = new byte[78];
		last.CopyTo(end, 0);
		BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(38), (uint)(trailer.Length + end.Length));
		BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(42), 3);
		"8db42d694ccc418790edff439fe026bf"u8.CopyTo(end.AsSpan(46));

		var file = new MemoryStream();
		file.Write(new byte[mediaBytes]);
		file.Write(trailer.ToArray());
		file.Write(end);
		file.Position = 0;
		return file;
	}

	private static byte[] Footer(ushort id, int length)
	{
		var footer = new byte[6];
		BinaryPrimitives.WriteUInt16LittleEndian(footer, id);
		BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(2), (uint)length);
		return footer;
	}

	private static byte[] Info(string model)
	{
		List<byte> info = [0x0a, 3, (byte)'S', (byte)'N', (byte)'1', 0x12, (byte)model.Length];
		info.AddRange(Encoding.UTF8.GetBytes(model));
		return [.. info];
	}

	/// <summary>1 kHz samples from `startMicros` for `count` ms, accel X = the sample's index (so averages are checkable).</summary>
	private static byte[] PackedImu(long startMicros, int count)
	{
		var data = new byte[count * 20];
		for (var i = 0; i < count; i++)
		{
			Span<byte> e = data.AsSpan(i * 20, 20);
			BinaryPrimitives.WriteInt64LittleEndian(e, startMicros + i * 1000L);
			BinaryPrimitives.WriteUInt16LittleEndian(e[8..], (ushort)(0x8000 + i));
			BinaryPrimitives.WriteUInt16LittleEndian(e[10..], 0x8000);
			BinaryPrimitives.WriteUInt16LittleEndian(e[12..], 0x8000 + 1000);
			// Gyro at rest - 0x8000, never three zero bytes where doubles would have them.
			for (var g = 14; g < 20; g += 2) BinaryPrimitives.WriteUInt16LittleEndian(e[g..], 0x8000);
		}

		return data;
	}

	private static byte[] DoubleImu(long startMicros, int count)
	{
		var data = new byte[count * 56];
		for (var i = 0; i < count; i++)
		{
			Span<byte> e = data.AsSpan(i * 56, 56);
			BinaryPrimitives.WriteInt64LittleEndian(e, startMicros + i * 1000L);
			BinaryPrimitives.WriteDoubleLittleEndian(e[8..], i / 1000.0);
			BinaryPrimitives.WriteDoubleLittleEndian(e[24..], 1);
		}

		return data;
	}

	private static byte[] Exposures(long startMicros, int count)
	{
		var data = new byte[count * 16];
		for (var i = 0; i < count; i++)
		{
			BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(i * 16), startMicros + i * FrameMicros);
			BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(i * 16 + 8), 1.0 / 2000);
		}

		return data;
	}

	[TestMethod]
	[DataRow(true)]
	[DataRow(false)]
	public void ReadsModelImuAndExposures_WithAndWithoutDirectoryTable(bool directory)
	{
		using MemoryStream file = File([(0x101, Info("Insta360 X4")), (0x300, PackedImu(5_000_000, 200)), (0x400, Exposures(4_997_000, 10))], directory);

		Insta360Trailer trailer = Insta360TrailerParser.Read(file)!;

		Assert.AreEqual("Insta360 X4", trailer.Model);
		Assert.AreEqual(200, trailer.Imu.Count);
		Assert.AreEqual(0.005, trailer.Imu[5].AccelX, 1e-9, "packed values are (raw - 0x8000) / 1000");
		Assert.AreEqual(1.0, trailer.Imu[5].AccelZ, 1e-9);
		Assert.AreEqual(10, trailer.Exposures.Count);
		Assert.AreEqual(1.0 / 2000, trailer.Exposures[3].ExposureSeconds, 1e-12);
	}

	[TestMethod]
	public void NoTrailer_IsNull()
	{
		using var file = new MemoryStream(new byte[500]);

		Assert.IsNull(Insta360TrailerParser.Read(file));
	}

	[TestMethod]
	public void ImuEntrySize_TellsDoublesFromPacked()
	{
		Assert.AreEqual(56, Insta360TrailerParser.ImuEntrySize(DoubleImu(0, 3)));
		Assert.AreEqual(20, Insta360TrailerParser.ImuEntrySize(PackedImu(0, 3)));
		// 280 bytes fits both sizes - the zeros doubles leave at bytes 16-18 decide.
		Assert.AreEqual(56, Insta360TrailerParser.ImuEntrySize(DoubleImu(0, 5)));
		Assert.AreEqual(20, Insta360TrailerParser.ImuEntrySize(PackedImu(0, 14)));
	}

	[TestMethod]
	public void ToFrames_OnePerExposure_AccelAveragedOverTheFrame()
	{
		// IMU samples at 0, 1, ... ms; exposures every 16.683 ms from 0.
		var trailer = new Insta360Trailer("Insta360 X4",
			[.. Enumerable.Range(0, 200).Select(i => new Insta360ImuSample(i * 1000L, i, 0, 1))],
			[.. Enumerable.Range(0, 10).Select(i => new Insta360Exposure(i * FrameMicros, 0.001))]);

		List<TelemetryFrame> frames = Insta360TrailerParser.ToFrames(trailer);

		Assert.AreEqual(10, frames.Count);
		Assert.AreEqual(0, frames[0].SampleTimeSeconds);
		Assert.AreEqual(FrameMicros / 1e6, frames[1].SampleTimeSeconds, 1e-9);
		// Frame 1 covers samples 17..33 (16.683 ms up to 33.366 ms).
		Assert.AreEqual(25, frames[1].AccelX, 1e-9);
		Assert.AreEqual(0.001, frames[1].ShutterSeconds);
		Assert.IsFalse(frames.Any(f => f.HasGpsFix));
	}

	[TestMethod]
	public void ToFrames_StopsAtTheVideosLength()
	{
		// A few more exposures than video frames - the file's own duration decides.
		var trailer = new Insta360Trailer(null, [],
			[.. Enumerable.Range(0, 188).Select(i => new Insta360Exposure(i * FrameMicros, 0.001))]);

		List<TelemetryFrame> frames = Insta360TrailerParser.ToFrames(trailer, 3.0);

		Assert.AreEqual(180, frames.Count, 1);
		Assert.AreEqual(3.0, frames[^1].SampleTimeSeconds, FrameMicros / 1e6);
	}
}
