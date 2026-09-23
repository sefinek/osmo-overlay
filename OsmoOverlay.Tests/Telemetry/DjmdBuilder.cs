using System.Text;

namespace OsmoOverlay.Tests.Telemetry;

/// <summary>Minimal protobuf writer for building synthetic djmd samples - no real recording (GPS, serial number) in the repo.</summary>
internal sealed class Proto
{
	private readonly List<byte> _bytes = [];

	public Proto Varint(int field, ulong value)
	{
		Tag(field, 0);
		WriteVarint(_bytes, value);
		return this;
	}

	public Proto Double(int field, double value)
	{
		Tag(field, 1);
		_bytes.AddRange(BitConverter.GetBytes(value));
		return this;
	}

	public Proto Float(int field, float value)
	{
		Tag(field, 5);
		_bytes.AddRange(BitConverter.GetBytes(value));
		return this;
	}

	public Proto Bytes(int field, byte[] value)
	{
		Tag(field, 2);
		WriteVarint(_bytes, (ulong)value.Length);
		_bytes.AddRange(value);
		return this;
	}

	public Proto Message(int field, Proto inner)
	{
		return Bytes(field, inner.ToArray());
	}

	public Proto String(int field, string value)
	{
		return Bytes(field, Encoding.UTF8.GetBytes(value));
	}

	public byte[] ToArray()
	{
		return [.. _bytes];
	}

	/// <summary>Varints back to back with no tags - how djmd stores the shutter speed fraction.</summary>
	public static byte[] RawVarints(params ulong[] values)
	{
		List<byte> bytes = [];
		foreach (var v in values) WriteVarint(bytes, v);
		return [.. bytes];
	}

	private void Tag(int field, int wireType)
	{
		WriteVarint(_bytes, (ulong)((field << 3) | wireType));
	}

	private static void WriteVarint(List<byte> bytes, ulong value)
	{
		while (value >= 0x80)
		{
			bytes.Add((byte)(value | 0x80));
			value >>= 7;
		}

		bytes.Add((byte)value);
	}
}

/// <summary>One djmd sample laid out as documented at the top of DjiMetaTelemetryParser.</summary>
internal sealed record DjmdSample
{
	public int FixType { get; init; } = 3;
	public double Lat { get; init; } = 50.061;
	public double Lon { get; init; } = 19.938;
	public long AltitudeMm { get; init; } = 219_500;
	public string? Timestamp { get; init; } = "2026-09-23 17:59:25";
	public float Vx { get; init; } = 3f;
	public float Vy { get; init; } = 4f;
	public float Iso { get; init; } = 100f;
	public (ulong Num, ulong Den) Shutter { get; init; } = (1, 240);
	public ulong ColorTemperature { get; init; } = 5600;
	public (float X, float Y, float Z) Accel { get; init; } = (0.1f, -0.2f, 0.98f);
	public string? DeviceName { get; init; } = "DJI TEST";

	public Proto ToProto()
	{
		Proto camera = new Proto()
			.Message(3, new Proto().Float(1, Iso))
			.Message(4, new Proto().Bytes(1, Proto.RawVarints(Shutter.Num, Shutter.Den)))
			.Message(6, new Proto().Varint(1, ColorTemperature))
			.Message(10, new Proto().Float(2, Accel.X).Float(3, Accel.Y).Float(4, Accel.Z));

		Proto fix = new Proto()
			.Message(1, new Proto().Varint(1, (ulong)FixType).Double(2, Lat).Double(3, Lon))
			.Varint(2, (ulong)AltitudeMm);
		if (Timestamp is not null) fix.Message(6, new Proto().String(1, Timestamp));

		Proto gps = new();
		if (DeviceName is not null) gps.Message(1, new Proto().String(4, DeviceName));
		gps.Message(2, fix).Message(3, new Proto().Float(1, Vx).Float(2, Vy));

		return new Proto().Message(1, new Proto().Varint(1, 7)).Message(2, camera).Message(4, gps);
	}

	/// <summary>A whole djmd stream: one top-level field-3 message per sample.</summary>
	public static byte[] Stream(params DjmdSample[] samples)
	{
		Proto stream = new();
		foreach (DjmdSample s in samples) stream.Message(3, s.ToProto());
		return stream.ToArray();
	}
}
