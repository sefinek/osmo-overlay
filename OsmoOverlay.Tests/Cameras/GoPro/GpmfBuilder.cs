using System.Buffers.Binary;
using System.Text;

namespace OsmoOverlay.Tests.Cameras.GoPro;

/// <summary>GPMF bytes for the tests: KLV items, big endian, padded to four bytes.</summary>
internal static class GpmfBuilder
{
	public static byte[] Item(string key, char type, int size, int repeat, byte[] data)
	{
		byte[] bytes = new byte[8 + (data.Length + 3) / 4 * 4];
		Encoding.ASCII.GetBytes(key).CopyTo(bytes, 0);
		bytes[4] = (byte)type;
		bytes[5] = (byte)size;
		BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), (ushort)repeat);
		data.CopyTo(bytes, 8);
		return bytes;
	}

	public static byte[] Container(string key, params byte[][] children)
	{
		byte[] data = [.. children.SelectMany(c => c)];
		return Item(key, '\0', 1, data.Length, data);
	}

	public static byte[] Text(string key, string value)
	{
		return Item(key, 'c', value.Length, 1, Encoding.ASCII.GetBytes(value));
	}

	public static byte[] Shorts(string key, int axes, params short[] values)
	{
		byte[] data = new byte[values.Length * 2];
		for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(i * 2), values[i]);
		return Item(key, 's', axes * 2, values.Length / axes, data);
	}

	public static byte[] Longs(string key, params long[] values)
	{
		byte[] data = new byte[values.Length * 8];
		for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(i * 8), values[i]);
		return Item(key, 'J', 8, values.Length, data);
	}

	public static byte[] Floats(string key, int axes, params float[] values)
	{
		byte[] data = new byte[values.Length * 4];
		for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(i * 4), values[i]);
		return Item(key, 'f', axes * 4, values.Length / axes, data);
	}

	/// <summary>A structure of int32 fields (and uint16 for the ones named 'S') - GPS9's "lllllllSS".</summary>
	public static byte[] Structures(string key, string types, params long[][] rows)
	{
		int size = types.Sum(t => t == 'S' ? 2 : 4);
		byte[] data = new byte[rows.Length * size];
		int at = 0;
		foreach (long[] row in rows)
		{
			for (int field = 0; field < types.Length; field++)
			{
				if (types[field] == 'S')
				{
					BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at), (ushort)row[field]);
					at += 2;
				}
				else
				{
					BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(at), (int)row[field]);
					at += 4;
				}
			}
		}

		return Item(key, '?', size, rows.Length, data);
	}
}
