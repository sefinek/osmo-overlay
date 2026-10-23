using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace OsmoOverlay.Cameras.GoPro;

/// <summary>
///     One GPMF stream (a STRM): the sensor's samples, scaled (SCAL), with the metadata that came before them. `Values`
///     holds Count samples of Axes numbers each, one after another.
/// </summary>
internal sealed record GpmfStream(
	string Key,
	int Axes,
	double[] Values,
	ulong? StampMicroseconds,
	string? InputOrientation,
	string? GpsUtc,
	int? GpsFix,
	double? GpsPrecision,
	double? TemperatureCelsius = null)
{
	public int Count => Axes == 0 ? 0 : Values.Length / Axes;
}

/// <summary>One device's (DEVC) streams in one packet - `Name` is its DVNM, the camera's model for the camera's own device.</summary>
internal sealed record GpmfDevice(string? Name, IReadOnlyList<GpmfStream> Streams);

/// <summary>
///     GoPro's GPMF: a stream of KLV items - a four-character key, a type character, the size of one structure and how
///     many there are (big endian), the data padded to four bytes. A type of 0 is a container (DEVC holds a device's STRMs).
///     The data of a STRM is its last item; the ones before it are its metadata (scale, orientation, timestamps...).
///     Types: b B s S l L j J (integers), f d, q Q (fixed point 15.16 and 31.32), c U (text), F G, and ? (a structure
///     whose fields are named by the TYPE item before it, e.g. GPS9's "lllllllSS"). A packet that ends in a half item (the
///     last one of a cut file) gives what was read before it.
/// </summary>
internal static class GpmfReader
{
	private const int HeaderSize = 8;

	public static List<GpmfDevice> Read(ReadOnlySpan<byte> packet)
	{
		List<GpmfDevice> devices = [];
		foreach (Item item in Items(packet, true))
		{
			if (item.Key == "DEVC" && item.Type == 0) devices.Add(ReadDevice(item.Data));
		}

		return devices;
	}

	private static GpmfDevice ReadDevice(ReadOnlySpan<byte> data)
	{
		string? name = null;
		List<GpmfStream> streams = [];
		foreach (Item item in Items(data))
		{
			if (item.Type == 0 && item.Key == "STRM")
			{
				if (ReadStream(item.Data) is { } stream) streams.Add(stream);
			}
			else if (item.Key == "DVNM" && item.Type == 'c')
			{
				name = Text(item.Data);
			}
		}

		return new GpmfDevice(name, streams);
	}

	private static GpmfStream? ReadStream(ReadOnlySpan<byte> data)
	{
		Item last = default;
		bool any = false;
		double[] scale = [];
		string? complexType = null, orientation = null, utc = null;
		ulong? stamp = null;
		int? fix = null;
		double? precision = null, temperature = null;

		foreach (Item item in Items(data))
		{
			if (any)
			{
				switch (last.Key)
				{
					case "SCAL": scale = Numbers(last, []).Values; break;
					case "TYPE": complexType = Text(last.Data); break;
					case "ORIN": orientation = Text(last.Data); break;
					case "STMP" when Numbers(last, []).Values is [var micros, ..]: stamp = (ulong)micros; break;
					case "GPSU": utc = Text(last.Data); break;
					case "GPSF" when Numbers(last, []).Values is [var f, ..]: fix = (int)f; break;
					case "GPSP" when Numbers(last, []).Values is [var p, ..]: precision = p / 100.0; break;
					case "TMPC" when Numbers(last, []).Values is [var celsius, ..] && double.IsFinite(celsius) && celsius >= -273.15: temperature = celsius; break;
				}
			}

			last = item;
			any = true;
		}

		if (!any || last.Type == 0 || last.Type == 'c' || last.Type == 'U') return null;

		(double[] values, int axes) = last.Type == '?' ? Complex(last, complexType, scale) : Numbers(last, scale);
		return axes == 0 ? null : new GpmfStream(last.Key, axes, values, stamp, orientation, utc, fix, precision, temperature);
	}

	private static (double[] Values, int Axes) Numbers(Item item, double[] scale)
	{
		int width = TypeWidth(item.Type);
		if (width == 0 || item.Size % width != 0) return ([], 0);

		int axes = item.Size / width;
		double[] values = new double[item.Repeat * axes];
		for (int i = 0; i < values.Length; i++)
			values[i] = Scaled(Value(item.Data.AsSpan(i * width), item.Type), scale, i % axes);

		return (values, axes);
	}

	private static (double[] Values, int Axes) Complex(Item item, string? types, double[] scale)
	{
		if (string.IsNullOrEmpty(types) || types.Any(c => TypeWidth(c) == 0)) return ([], 0);
		if (types.Sum(c => TypeWidth(c)) != item.Size) return ([], 0);

		double[] values = new double[item.Repeat * types.Length];
		for (int i = 0; i < item.Repeat; i++)
		{
			int at = i * item.Size;
			for (int field = 0; field < types.Length; field++)
			{
				values[i * types.Length + field] = Scaled(Value(item.Data.AsSpan(at), types[field]), scale, field);
				at += TypeWidth(types[field]);
			}
		}

		return (values, types.Length);
	}

	private static double Scaled(double value, double[] scale, int axis)
	{
		if (scale.Length == 0) return value;

		double divisor = scale[axis % scale.Length];
		return divisor == 0 ? value : value / divisor;
	}

	private static int TypeWidth(int type)
	{
		return type switch
		{
			'b' or 'B' => 1,
			's' or 'S' => 2,
			'l' or 'L' or 'f' or 'q' => 4,
			'd' or 'j' or 'J' or 'Q' => 8,
			_ => 0
		};
	}

	private static double Value(ReadOnlySpan<byte> data, int type)
	{
		return type switch
		{
			'b' => (sbyte)data[0],
			'B' => data[0],
			's' => BinaryPrimitives.ReadInt16BigEndian(data),
			'S' => BinaryPrimitives.ReadUInt16BigEndian(data),
			'l' => BinaryPrimitives.ReadInt32BigEndian(data),
			'L' => BinaryPrimitives.ReadUInt32BigEndian(data),
			'f' => BinaryPrimitives.ReadSingleBigEndian(data),
			'd' => BinaryPrimitives.ReadDoubleBigEndian(data),
			'j' => BinaryPrimitives.ReadInt64BigEndian(data),
			'J' => BinaryPrimitives.ReadUInt64BigEndian(data),
			'q' => BinaryPrimitives.ReadInt32BigEndian(data) / 65536.0,
			'Q' => BinaryPrimitives.ReadInt64BigEndian(data) / 4294967296.0,
			_ => 0
		};
	}

	private static string Text(ReadOnlySpan<byte> data)
	{
		return Encoding.Latin1.GetString(data).TrimEnd('\0', ' ');
	}

	/// <summary>
	///     The items of one container, up to the first one that doesn't fit in it. A container that runs past the end is cut
	///     short when `keepCutContainer` (a file that ended in the middle of its last packet) - its own streams that were
	///     whole are read, the one that was cut isn't.
	/// </summary>
	private static List<Item> Items(ReadOnlySpan<byte> data, bool keepCutContainer = false)
	{
		List<Item> items = [];
		int at = 0;
		while (at + HeaderSize <= data.Length)
		{
			int size = data[at + 5];
			int repeat = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 6)..]);
			int length = size * repeat;
			int available = Math.Min(length, data.Length - at - HeaderSize);
			if (available < length && !(keepCutContainer && data[at + 4] == 0)) break;

			items.Add(new Item(Encoding.Latin1.GetString(data.Slice(at, 4)), data[at + 4], size, repeat, data.Slice(at + HeaderSize, available).ToArray()));
			at += HeaderSize + (length + 3) / 4 * 4;
		}

		return items;
	}

	private readonly record struct Item(string Key, int Type, int Size, int Repeat, byte[] Data);

	/// <summary>GPSU's "yymmddhhmmss.sss" as UTC - null for anything else.</summary>
	public static DateTime? ParseGpsUtc(string? text)
	{
		return text is { Length: >= 12 } && DateTime.TryParseExact(text[..12], "yyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime whole)
			? whole + TimeSpan.FromSeconds(text.Length > 13 && double.TryParse(text.AsSpan(12), NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction) ? fraction : 0)
			: null;
	}
}
