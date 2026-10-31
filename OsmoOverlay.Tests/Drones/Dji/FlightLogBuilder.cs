using System.Buffers.Binary;
using System.Security.Cryptography;
using OsmoOverlay.Drones.Dji;

namespace OsmoOverlay.Tests.Drones.Dji;

/// <summary>A synthetic flight log in DJI's layout: version 12 (xor only) or 14 (xor and AES with the given keys).</summary>
internal sealed class FlightLogBuilder(int version, DateTime started)
{
	public const byte Osd = 1, Home = 2, Custom = 5, Camera = 25, Battery = 22, KeyRecover = 50, KeyStorage = 56;

	private readonly List<byte> _records = [];
	private readonly Dictionary<int, byte[]> _ivs = [];
	private byte _seed = 7;

	/// <summary>The flight's length in the details block; 0 leaves the totals out.</summary>
	public double DurationSeconds { get; set; }

	public Dictionary<int, FeatureKey> Keys { get; } = new()
	{
		[FlightLogFeaturePoints.Base] = new FeatureKey(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), Enumerable.Range(40, 16).Select(i => (byte)i).ToArray()),
		[FlightLogFeaturePoints.DjiFlyCustom] = new FeatureKey(Enumerable.Range(60, 32).Select(i => (byte)i).ToArray(), Enumerable.Range(90, 16).Select(i => (byte)i).ToArray()),
		[FlightLogFeaturePoints.Camera] = new FeatureKey(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray(), Enumerable.Range(130, 16).Select(i => (byte)i).ToArray()),
		[FlightLogFeaturePoints.AirLink] = new FeatureKey(Enumerable.Range(200, 32).Select(i => (byte)i).ToArray(), Enumerable.Range(10, 16).Select(i => (byte)i).ToArray()),
		[FlightLogFeaturePoints.Battery] = new FeatureKey(Enumerable.Range(150, 32).Select(i => (byte)i).ToArray(), Enumerable.Range(190, 16).Select(i => (byte)i).ToArray())
	};

	public FlightLogBuilder Add(byte type, byte[] content)
	{
		byte seed = _seed++;
		int featurePoint = FlightLogFeaturePoints.FromRecordType(type, version);
		byte[] body;
		if (version > 12 && featurePoint != FlightLogFeaturePoints.Plaintext)
		{
			FeatureKey key = Keys[featurePoint];
			byte[] iv = _ivs.GetValueOrDefault(featurePoint, key.Iv);
			using var aes = Aes.Create();
			aes.Key = key.Key;
			byte[] cipher = aes.EncryptCbc(content, iv, PaddingMode.PKCS7);
			_ivs[featurePoint] = cipher[^16..];
			FlightLogCrypto.Xor(cipher, seed, type);
			body = [seed, .. cipher, 0x00];
		}
		else
		{
			byte[] data = [.. content, 0x00];
			FlightLogCrypto.Xor(data, seed, type);
			body = [seed, .. data];
		}

		_records.Add(type);
		if (version > 12) _records.AddRange(BitConverter.GetBytes((ushort)body.Length));
		else _records.Add((byte)body.Length);
		_records.AddRange(body);
		_records.Add(0xFF);
		return this;
	}

	public FlightLogBuilder AddKeyBlock(int featurePoint, byte[] data)
	{
		return Add(KeyStorage, [.. BitConverter.GetBytes((ushort)featurePoint), .. BitConverter.GetBytes((ushort)data.Length), .. data]);
	}

	public FlightLogBuilder NewSession()
	{
		_records.AddRange([KeyRecover, 2, 0, 0, 0, 0xFF]);
		return this;
	}

	public byte[] Build()
	{
		var file = new List<byte>(new byte[FlightLog.PrefixSize]);
		byte[] details = new byte[436];
		BinaryPrimitives.WriteInt64LittleEndian(details.AsSpan(4 * 20 + 3 + 2 * 4), new DateTimeOffset(started).ToUnixTimeMilliseconds());
		if (DurationSeconds > 0) BinaryPrimitives.WriteUInt32LittleEndian(details.AsSpan(119), (uint)(DurationSeconds * 1000));
		int recordsOffset;
		if (version > 12)
		{
			byte[] data = [0, .. BitConverter.GetBytes((ushort)details.Length), .. details, 0, 0];
			byte seed = 149;
			byte[] xored = data.ToArray();
			FlightLogCrypto.Xor(xored, seed, 0);
			file.AddRange([0, .. BitConverter.GetBytes((ushort)(xored.Length + 1)), seed, .. xored]);
			file.AddRange([1, 4, 0, .. BitConverter.GetBytes((ushort)4), 3, 0]);
			recordsOffset = file.Count;
		}
		else
		{
			file.AddRange(details);
			recordsOffset = file.Count;
		}

		file.AddRange(_records);
		byte[] bytes = file.ToArray();
		BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)(version > 12 ? recordsOffset : FlightLog.PrefixSize));
		BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 436);
		bytes[10] = (byte)version;
		return bytes;
	}

	public static byte[] OsdRecord(double latitude, double longitude, double heightMeters, double speedXMs, double speedYMs, int satellites, bool gpsValid, int battery = 0, int mode = 0, bool motorsOn = false, int gpsLevel = 0)
	{
		byte[] c = new byte[50];
		BitConverter.GetBytes(longitude * Math.PI / 180).CopyTo(c, 0);
		BitConverter.GetBytes(latitude * Math.PI / 180).CopyTo(c, 8);
		BitConverter.GetBytes((short)Math.Round(heightMeters * 10)).CopyTo(c, 16);
		BitConverter.GetBytes((short)Math.Round(speedXMs * 10)).CopyTo(c, 18);
		BitConverter.GetBytes((short)Math.Round(speedYMs * 10)).CopyTo(c, 20);
		c[30] = (byte)mode;
		c[32] = (byte)(motorsOn ? 0x04 : 0);
		c[33] = (byte)(gpsValid ? 0x80 : 0);
		c[34] = (byte)(0x90 | gpsLevel << 2);
		c[36] = (byte)satellites;
		c[40] = (byte)battery;
		return c;
	}

	public static byte[] HomeRecord(double altitudeMeters)
	{
		byte[] c = new byte[30];
		BitConverter.GetBytes((float)(altitudeMeters * 10)).CopyTo(c, 16);
		return c;
	}

	public static byte[] CustomRecord(DateTime utc)
	{
		byte[] c = new byte[18];
		BitConverter.GetBytes(new DateTimeOffset(utc).ToUnixTimeMilliseconds()).CopyTo(c, 10);
		return c;
	}

	/// <summary>The pack record (type 22, magic 2): millivolts at 2, milliamps (negative while discharging) at 6, tenths of a degree at 18, the percent at 21.</summary>
	public static byte[] BatteryRecord(int percent, int millivolts = 11000, int milliamps = -2000, int deciDegrees = 253)
	{
		byte[] c = new byte[42];
		c[0] = 2;
		BitConverter.GetBytes(millivolts).CopyTo(c, 2);
		BitConverter.GetBytes(milliamps).CopyTo(c, 6);
		BitConverter.GetBytes(3138).CopyTo(c, 10);
		BitConverter.GetBytes(1500).CopyTo(c, 14);
		BitConverter.GetBytes((short)deciDegrees).CopyTo(c, 18);
		c[21] = (byte)percent;
		return c;
	}

	/// <summary>The cells record (type 22, magic 3): the count at 2, then each cell in millivolts.</summary>
	public static byte[] CellsRecord(params int[] millivolts)
	{
		byte[] c = new byte[3 + millivolts.Length * 2 + 2];
		c[0] = 3;
		c[2] = (byte)millivolts.Length;
		for (int i = 0; i < millivolts.Length; i++) BitConverter.GetBytes((ushort)millivolts[i]).CopyTo(c, 3 + i * 2);
		return c;
	}

	public static byte[] CameraRecord(bool recording)
	{
		return [(byte)(recording ? 0x40 : 0), 0, 0, 0];
	}
}
