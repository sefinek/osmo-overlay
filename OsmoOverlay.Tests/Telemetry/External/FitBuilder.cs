using System.Buffers.Binary;

namespace OsmoOverlay.Tests.Telemetry.External;

/// <summary>A FIT activity file for the tests: records (message 20) and timer events (message 21), with the header and checksum.</summary>
internal sealed class FitBuilder
{
	// 1989-12-31 00:00 UTC is FIT's time 0.
	private static readonly DateTime Epoch = new(1989, 12, 31, 0, 0, 0, DateTimeKind.Utc);

	public const int RecordLocal = 0;
	public const int EventLocal = 1;
	public const int CompressedLocal = 2;

	private readonly List<byte> _data = [];

	public static uint Seconds(DateTime utc)
	{
		return (uint)(utc - Epoch).TotalSeconds;
	}

	public FitBuilder DefineRecord(bool big = false, bool developerField = false)
	{
		Define(RecordLocal, 20, big, developerField, (253, 4, 6), (0, 4, 5), (1, 4, 5), (78, 4, 6), (73, 4, 6));
		return this;
	}

	public FitBuilder DefineEvent()
	{
		Define(EventLocal, 21, false, false, (253, 4, 6), (0, 1, 0), (1, 1, 0));
		return this;
	}

	public FitBuilder DefineSensorRecord()
	{
		Define(RecordLocal, 20, false, false, (253, 4, 6), (0, 4, 5), (1, 4, 5), (78, 4, 6), (73, 4, 6), (5, 4, 6), (13, 1, 1));
		return this;
	}

	public FitBuilder SensorRecord(DateTime utc, double lat, double lon, double? distanceMeters, sbyte? temperature)
	{
		Record(utc, lat, lon, 80, 2);
		Number(distanceMeters is { } meters ? (uint)Math.Round(meters * 100) : uint.MaxValue, 4, false);
		_data.Add(temperature is { } celsius ? unchecked((byte)celsius) : (byte)127);
		return this;
	}

	public FitBuilder DefineBattery(bool deviceInfo = false)
	{
		if (deviceInfo) Define(3, 23, false, false, (253, 4, 6), (0, 1, 2), (32, 1, 2));
		else Define(3, 104, false, false, (253, 4, 6), (2, 1, 2));
		return this;
	}

	public FitBuilder Battery(DateTime utc, byte percent, byte? deviceIndex = null)
	{
		_data.Add(3);
		Number(Seconds(utc), 4, false);
		if (deviceIndex is { } index) _data.Add(index);
		_data.Add(percent);
		return this;
	}

	public FitBuilder DefineCompressedRecord(bool withTimestamp = false)
	{
		Define(CompressedLocal, 20, false, false, withTimestamp ? [(253, 4, 6), (0, 4, 5), (1, 4, 5)] : [(0, 4, 5), (1, 4, 5)]);
		return this;
	}

	public FitBuilder Record(DateTime utc, double lat, double lon, double altitudeMeters, double speedMs, bool big = false, bool developerField = false)
	{
		_data.Add(RecordLocal);
		Number(Seconds(utc), 4, big);
		Number((uint)(int)Math.Round(lat / 180.0 * 2147483648.0), 4, big);
		Number((uint)(int)Math.Round(lon / 180.0 * 2147483648.0), 4, big);
		Number((uint)Math.Round((altitudeMeters + 500) * 5), 4, big);
		Number((uint)Math.Round(speedMs * 1000), 4, big);
		if (developerField) _data.AddRange(new byte[] { 0xAA, 0xBB, 0xCC });
		return this;
	}

	public FitBuilder RecordWithoutPosition(DateTime utc)
	{
		_data.Add(RecordLocal);
		Number(Seconds(utc), 4, false);
		Number(0x7FFFFFFF, 4, false);
		Number(0x7FFFFFFF, 4, false);
		Number(0xFFFFFFFF, 4, false);
		Number(0xFFFFFFFF, 4, false);
		return this;
	}

	public FitBuilder CompressedRecord(int timeOffset, double lat, double lon)
	{
		_data.Add((byte)(0x80 | (CompressedLocal << 5) | timeOffset));
		Number((uint)(int)Math.Round(lat / 180.0 * 2147483648.0), 4, false);
		Number((uint)(int)Math.Round(lon / 180.0 * 2147483648.0), 4, false);
		return this;
	}

	public FitBuilder TimerEvent(DateTime utc, byte eventType, byte eventNumber = 0)
	{
		_data.Add(EventLocal);
		Number(Seconds(utc), 4, false);
		_data.Add(eventNumber);
		_data.Add(eventType);
		return this;
	}

	public byte[] ToArray(bool breakChecksum = false)
	{
		byte[] header = new byte[14];
		header[0] = 14;
		header[1] = 0x20;
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)_data.Count);
		".FIT"u8.CopyTo(header.AsSpan(8));

		byte[] body = [.. header, .. _data];
		byte[] file = new byte[body.Length + 2];
		body.CopyTo(file, 0);
		BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(body.Length), (ushort)(Crc(body) ^ (breakChecksum ? 1 : 0)));
		return file;
	}

	private void Define(int local, int message, bool big, bool developerField, params (int Number, int Size, int Type)[] fields)
	{
		_data.Add((byte)(0x40 | (developerField ? 0x20 : 0) | local));
		_data.Add(0);
		_data.Add((byte)(big ? 1 : 0));
		byte[] id = new byte[2];
		if (big) BinaryPrimitives.WriteUInt16BigEndian(id, (ushort)message);
		else BinaryPrimitives.WriteUInt16LittleEndian(id, (ushort)message);
		_data.AddRange(id);
		_data.Add((byte)fields.Length);
		foreach ((int number, int size, int type) in fields) _data.AddRange(new[] { (byte)number, (byte)size, (byte)type });

		if (developerField)
		{
			_data.Add(1);
			_data.AddRange(new byte[] { 0, 3, 0 });
		}
	}

	private void Number(uint value, int size, bool big)
	{
		byte[] bytes = new byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
		if (big) Array.Reverse(bytes);
		_data.AddRange(big ? bytes.AsSpan(4 - size).ToArray() : bytes.AsSpan(0, size).ToArray());
	}

	private static ushort Crc(byte[] bytes)
	{
		ushort[] table = [0x0000, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401, 0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400];
		ushort crc = 0;
		foreach (byte b in bytes)
		{
			crc = (ushort)((crc >> 4) ^ table[crc & 0xF] ^ table[b & 0xF]);
			crc = (ushort)((crc >> 4) ^ table[crc & 0xF] ^ table[(b >> 4) & 0xF]);
		}

		return crc;
	}
}
