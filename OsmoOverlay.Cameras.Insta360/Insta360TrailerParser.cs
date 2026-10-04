using System.Buffers.Binary;
using System.Text;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Cameras.Insta360;

/// <param name="TimeMicros">The camera's own clock (microseconds since it powered on), not wall-clock time.</param>
internal readonly record struct Insta360ImuSample(long TimeMicros, double AccelX, double AccelY, double AccelZ);

/// <param name="TimeMicros">When the sensor exposed a frame, on the same clock as the IMU - one per captured frame.</param>
internal readonly record struct Insta360Exposure(long TimeMicros, double ExposureSeconds);

internal sealed record Insta360Trailer(string? Model, List<Insta360ImuSample> Imu, List<Insta360Exposure> Exposures);

/// <summary>
///     Reads the telemetry Insta360 cameras append after the MP4's last box (.insv, .lrv - ffprobe doesn't see it). Layout,
///     cross-checked against exiftool's ProcessInsta360 and a real Insta360 X4 recording (fw 1.7.18):
///     - the file ends with 78 bytes: the last record's 6-byte footer, padding, the trailer's length (int32 at 38), a
///     version (int32 at 42) and the 32-character magic;
///     - every record is its data followed by a footer: record id (int16) and data length (int32), all little-endian;
///     - newer cameras (X4, Ace Pro) end with record 0x0, a table of (id int16, size int32, offset int32 from the trailer's
///     start) entries saying where each record is, instead of the records simply sitting back to back.
///     Records read here: 0x101 camera info (tag/length/value; 0x12 model - the serial at 0x0a is never read), 0x300 IMU
///     (time µs int64 + accelerometer XYZ in g + gyro XYZ in rad/s, either as 6 doubles in 56-byte entries or as
///     (raw - 0x8000) / 1000 in 20-byte ones - measured 1 kHz, |a| median 1.03 g), 0x400 exposure (time µs int64 +
///     exposure seconds double, one per sensor frame: 16.68 ms apart at 59.94 fps). Timestamps are microseconds -
///     exiftool prints them divided by 1000, as milliseconds. GPS (0x700) is only there with a GPS remote or the phone app
///     and isn't read yet.
/// </summary>
internal static class Insta360TrailerParser
{
	private const int FooterSize = 78;
	private const int RecordFooterSize = 6;
	private static ReadOnlySpan<byte> Magic => "8db42d694ccc418790edff439fe026bf"u8;

	private const ushort DirectoryRecord = 0x0;
	private const ushort InfoRecord = 0x101;
	private const ushort ImuRecord = 0x300;
	private const ushort ExposureRecord = 0x400;
	private const byte ModelTag = 0x12;

	/// <summary>Only the end block's magic - no records read.</summary>
	public static bool HasTrailer(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128);
		if (stream.Length < FooterSize) return false;

		Span<byte> magic = stackalloc byte[32];
		stream.Position = stream.Length - magic.Length;
		stream.ReadExactly(magic);
		return magic.SequenceEqual(Magic);
	}

	/// <summary>Null when the file has no Insta360 trailer.</summary>
	public static Insta360Trailer? Read(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
		return Read(stream);
	}

	public static Insta360Trailer? Read(Stream stream)
	{
		long fileLength = stream.Length;
		if (fileLength < FooterSize) return null;

		byte[] footer = new byte[FooterSize];
		stream.Position = fileLength - FooterSize;
		stream.ReadExactly(footer);
		if (!footer.AsSpan(FooterSize - Magic.Length).SequenceEqual(Magic)) return null;

		long trailerLength = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(38));
		if (trailerLength < FooterSize || trailerLength > fileLength)
			throw new InvalidDataException($"Insta360 trailer length {trailerLength} doesn't fit the file ({fileLength} bytes).");
		long trailerStart = fileLength - trailerLength;

		string? model = null;
		List<Insta360ImuSample> imu = [];
		List<Insta360Exposure> exposures = [];
		foreach ((ushort id, long dataStart, int length) in Records(stream, footer, trailerStart, fileLength - FooterSize))
		{
			switch (id)
			{
				case InfoRecord:
					model = ReadModel(ReadData(stream, dataStart, length));
					break;
				case ImuRecord:
					ReadImu(ReadData(stream, dataStart, length), imu);
					break;
				case ExposureRecord:
					ReadExposures(ReadData(stream, dataStart, length), exposures);
					break;
			}
		}

		imu.Sort((a, b) => a.TimeMicros.CompareTo(b.TimeMicros));
		exposures.Sort((a, b) => a.TimeMicros.CompareTo(b.TimeMicros));
		return new Insta360Trailer(model, imu, exposures);
	}

	/// <summary>Every record's id and where its data is - through the directory table when the trailer ends with one.</summary>
	private static IEnumerable<(ushort Id, long DataStart, int Length)> Records(Stream stream, byte[] footer, long trailerStart, long lastRecordEnd)
	{
		ushort id = BinaryPrimitives.ReadUInt16LittleEndian(footer);
		uint length = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(2));
		if (length > lastRecordEnd - trailerStart) throw new InvalidDataException("Insta360 trailer record runs past the trailer's start.");

		if (id == DirectoryRecord && length > 0)
		{
			byte[] table = ReadData(stream, lastRecordEnd - length, (int)length);
			byte[] header = new byte[RecordFooterSize];
			for (int p = 0; p + 10 <= table.Length; p += 10)
			{
				uint size = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(p + 2));
				uint offset = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(p + 6));
				if (BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(p)) == 0 || size == 0) continue;

				long dataStart = trailerStart + offset;
				if (dataStart + size + RecordFooterSize > lastRecordEnd) continue;

				// The table's own ids don't always match the records' (0x3 for 0x300) - the footer after the data says.
				stream.Position = dataStart + size;
				stream.ReadExactly(header);
				yield return (BinaryPrimitives.ReadUInt16LittleEndian(header), dataStart, (int)size);
			}

			yield break;
		}

		// Older cameras: records back to back, walked from the last one to the first.
		long end = lastRecordEnd;
		byte[] previous = new byte[RecordFooterSize];
		while (true)
		{
			long dataStart = end - length;
			if (dataStart < trailerStart) yield break;

			yield return (id, dataStart, (int)length);

			end = dataStart - RecordFooterSize;
			if (end < trailerStart) yield break;

			stream.Position = end;
			stream.ReadExactly(previous);
			id = BinaryPrimitives.ReadUInt16LittleEndian(previous);
			length = BinaryPrimitives.ReadUInt32LittleEndian(previous.AsSpan(2));
			if (length == 0 && id == 0) yield break;
		}
	}

	private static byte[] ReadData(Stream stream, long start, int length)
	{
		byte[] data = new byte[length];
		stream.Position = start;
		stream.ReadExactly(data);
		return data;
	}

	private static string? ReadModel(byte[] info)
	{
		for (int p = 0; p + 2 <= info.Length;)
		{
			byte tag = info[p];
			byte length = info[p + 1];
			if (p + 2 + length > info.Length) break;
			if (tag == ModelTag) return Encoding.UTF8.GetString(info, p + 2, length).Trim('\0', ' ');
			p += 2 + length;
		}

		return null;
	}

	private static void ReadImu(byte[] data, List<Insta360ImuSample> samples)
	{
		int entry = ImuEntrySize(data);
		for (int p = 0; p + entry <= data.Length; p += entry)
		{
			ReadOnlySpan<byte> e = data.AsSpan(p, entry);
			long time = BinaryPrimitives.ReadInt64LittleEndian(e);
			samples.Add(entry == 56
				? new Insta360ImuSample(time, BinaryPrimitives.ReadDoubleLittleEndian(e[8..]), BinaryPrimitives.ReadDoubleLittleEndian(e[16..]),
					BinaryPrimitives.ReadDoubleLittleEndian(e[24..]))
				: new Insta360ImuSample(time, Packed(e[8..]), Packed(e[10..]), Packed(e[12..])));
		}
	}

	/// <summary>Entries of 56 bytes (doubles) or 20 (packed) - by the length where only one fits, else like exiftool: doubles leave bytes 16-18 zero.</summary>
	internal static int ImuEntrySize(ReadOnlySpan<byte> data)
	{
		if (data.Length % 56 == 0 && data.Length % 20 != 0) return 56;
		if (data.Length % 20 == 0 && data.Length % 56 != 0) return 20;
		return data.Length >= 20 && data[16] == 0 && data[17] == 0 && data[18] == 0 ? 56 : 20;
	}

	private static double Packed(ReadOnlySpan<byte> value)
	{
		return (BinaryPrimitives.ReadUInt16LittleEndian(value) - 0x8000) / 1000.0;
	}

	private static void ReadExposures(byte[] data, List<Insta360Exposure> exposures)
	{
		for (int p = 0; p + 16 <= data.Length; p += 16)
		{
			exposures.Add(new Insta360Exposure(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(p)),
				BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(p + 8))));
		}
	}

	/// <summary>
	///     One telemetry frame per exposure (captured frame), from the first one on: the accelerometer averaged over the
	///     frame's own stretch of the 1 kHz IMU (what a per-frame reading means at 60 fps - no single sample's vibration),
	///     the frame's exposure as the shutter. SampleTimeSeconds is seconds since the first captured frame - the file's
	///     first video frame; there can be a few more exposures than video frames (1315 for 1307 on an X4), so frames
	///     past `durationSeconds` are left out when given. No GPS - every frame is flagged as having no fix.
	/// </summary>
	public static List<TelemetryFrame> ToFrames(Insta360Trailer trailer, double? durationSeconds = null)
	{
		List<Insta360Exposure> exposures = trailer.Exposures;
		List<TelemetryFrame> frames = [];
		if (exposures.Count == 0) return frames;

		long origin = exposures[0].TimeMicros;
		double frameMicros = exposures.Count > 1 ? (exposures[^1].TimeMicros - origin) / (double)(exposures.Count - 1) : 1e6 / 60;
		double halfFrameSeconds = frameMicros / 2e6;
		int imu = 0;
		for (int i = 0; i < exposures.Count; i++)
		{
			long start = exposures[i].TimeMicros;
			long end = i + 1 < exposures.Count ? exposures[i + 1].TimeMicros : start + (long)frameMicros;
			while (imu < trailer.Imu.Count && trailer.Imu[imu].TimeMicros < start) imu++;

			double x = 0, y = 0, z = 0;
			int count = 0;
			for (int k = imu; k < trailer.Imu.Count && trailer.Imu[k].TimeMicros < end; k++, count++)
			{
				x += trailer.Imu[k].AccelX;
				y += trailer.Imu[k].AccelY;
				z += trailer.Imu[k].AccelZ;
			}

			// Without IMU samples in its stretch (the IMU starts a few ms after the first frame) the nearest one stands in.
			if (count == 0 && trailer.Imu.Count > 0)
			{
				Insta360ImuSample nearest = trailer.Imu[Math.Min(imu, trailer.Imu.Count - 1)];
				(x, y, z, count) = (nearest.AccelX, nearest.AccelY, nearest.AccelZ, 1);
			}

			double time = (start - origin) / 1e6;
			if (durationSeconds is { } duration && time > duration + halfFrameSeconds) break;

			frames.Add(new TelemetryFrame(frames.Count, time, 0, 0, 0, null,
				count > 0 ? x / count : 0, count > 0 ? y / count : 0, count > 0 ? z / count : 0,
				ShutterSeconds: exposures[i].ExposureSeconds, HasGpsFix: false));
		}

		return frames;
	}
}
