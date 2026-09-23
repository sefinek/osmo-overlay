using System.Buffers.Binary;
using System.Text;

namespace OsmoOverlay.Core.Ffmpeg;

/// <summary>
///     Minimal ISO-BMFF (MP4) box tree for the post-render edits ffmpeg can't do itself (see
///     Mp4CameraMetadata/Mp4FastStart). Only the container boxes those edits need to walk into are parsed
///     into children; every other box - including ones this code knows nothing about - is kept as its raw
///     payload and written back byte for byte.
/// </summary>
internal sealed class Mp4Box
{
	private static readonly HashSet<string> ContainerTypes = ["moov", "trak", "mdia", "minf", "stbl", "edts", "dinf", "mvex"];

	private Mp4Box(string type, byte[]? payload, List<Mp4Box>? children)
	{
		Type = type;
		Payload = payload;
		Children = children;
	}

	public string Type { get; }

	/// <summary>Everything after the box header - null for a parsed container (see Children).</summary>
	public byte[]? Payload { get; set; }

	public List<Mp4Box>? Children { get; }

	public static Mp4Box Leaf(string type, byte[] payload)
	{
		return new Mp4Box(type, payload, null);
	}

	public static Mp4Box Parse(string type, ReadOnlySpan<byte> payload)
	{
		return ContainerTypes.Contains(type)
			? new Mp4Box(type, null, ParseChildren(payload))
			: new Mp4Box(type, payload.ToArray(), null);
	}

	public static List<Mp4Box> ParseChildren(ReadOnlySpan<byte> data)
	{
		List<Mp4Box> boxes = [];
		var pos = 0;
		while (pos + 8 <= data.Length)
		{
			long size = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
			var type = Encoding.Latin1.GetString(data.Slice(pos + 4, 4));
			var header = 8;
			if (size == 1)
			{
				size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(pos + 8)..]);
				header = 16;
			}
			else if (size == 0)
			{
				size = data.Length - pos;
			}

			if (size < header || pos + size > data.Length)
				throw new InvalidDataException($"Malformed MP4 box '{type}' at offset {pos}.");

			boxes.Add(Parse(type, data.Slice(pos + header, (int)(size - header))));
			pos += (int)size;
		}

		return boxes;
	}

	public Mp4Box? Child(string type)
	{
		return Children?.FirstOrDefault(c => c.Type == type);
	}

	public Mp4Box? Find(params string[] path)
	{
		Mp4Box? current = this;
		foreach (var type in path)
		{
			current = current?.Child(type);
			if (current is null) return null;
		}

		return current;
	}

	public long Size => 8 + (Payload?.Length ?? Children!.Sum(c => c.Size));

	public void WriteTo(Stream stream)
	{
		if (Size > uint.MaxValue) throw new InvalidOperationException($"MP4 box '{Type}' is too large to write with a 32-bit size.");

		Span<byte> header = stackalloc byte[8];
		BinaryPrimitives.WriteUInt32BigEndian(header, (uint)Size);
		Encoding.Latin1.GetBytes(Type, header[4..]);
		stream.Write(header);

		if (Payload is not null)
			stream.Write(Payload);
		else
			foreach (Mp4Box child in Children!)
				child.WriteTo(stream);
	}

	public byte[] ToBytes()
	{
		using var stream = new MemoryStream();
		WriteTo(stream);
		return stream.ToArray();
	}
}

/// <summary>A top-level box as found in a file: where it starts, how big it is and how long its header is.</summary>
internal readonly record struct Mp4TopLevelBox(string Type, long Offset, long Size, int HeaderSize);

internal static class Mp4File
{
	public static List<Mp4TopLevelBox> ReadTopLevel(Stream stream)
	{
		List<Mp4TopLevelBox> boxes = [];
		Span<byte> header = stackalloc byte[16];
		var pos = 0L;
		var length = stream.Length;
		while (pos + 8 <= length)
		{
			stream.Position = pos;
			stream.ReadExactly(header[..8]);
			long size = BinaryPrimitives.ReadUInt32BigEndian(header);
			var type = Encoding.Latin1.GetString(header.Slice(4, 4));
			var headerSize = 8;
			if (size == 1)
			{
				stream.ReadExactly(header.Slice(8, 8));
				size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
				headerSize = 16;
			}
			else if (size == 0)
			{
				size = length - pos;
			}

			if (size < headerSize || pos + size > length)
				throw new InvalidDataException($"Malformed top-level MP4 box '{type}' at offset {pos}.");

			boxes.Add(new Mp4TopLevelBox(type, pos, size, headerSize));
			pos += size;
		}

		return boxes;
	}

	public static Mp4Box ReadMoov(Stream stream, Mp4TopLevelBox moov)
	{
		var payload = new byte[moov.Size - moov.HeaderSize];
		stream.Position = moov.Offset + moov.HeaderSize;
		stream.ReadExactly(payload);
		return Mp4Box.Parse("moov", payload);
	}
}

/// <summary>Reading/writing the full-box fields the post-render edits touch (version-dependent layouts).</summary>
internal static class Mp4Fields
{
	public static uint MovieTimescale(Mp4Box mvhd)
	{
		var p = mvhd.Payload!;
		return BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(p[0] == 1 ? 20 : 12));
	}

	public static uint NextTrackId(Mp4Box mvhd)
	{
		var p = mvhd.Payload!;
		return BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(p.Length - 4));
	}

	public static void SetNextTrackId(Mp4Box mvhd, uint id)
	{
		var p = mvhd.Payload!;
		BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(p.Length - 4), id);
	}

	public static (uint Timescale, ulong Duration) MediaTiming(Mp4Box mdhd)
	{
		var p = mdhd.Payload!;
		return p[0] == 1
			? (BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(20)), BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(24)))
			: (BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12)), BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(16)));
	}

	public static void SetMediaDuration(Mp4Box mdhd, ulong duration)
	{
		var p = mdhd.Payload!;
		if (p[0] == 1) BinaryPrimitives.WriteUInt64BigEndian(p.AsSpan(24), duration);
		else BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), (uint)Math.Min(duration, uint.MaxValue));
	}

	public static void SetTrackIdAndDuration(Mp4Box tkhd, uint trackId, ulong duration)
	{
		var p = tkhd.Payload!;
		if (p[0] == 1)
		{
			BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(20), trackId);
			BinaryPrimitives.WriteUInt64BigEndian(p.AsSpan(28), duration);
		}
		else
		{
			BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12), trackId);
			BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(20), (uint)Math.Min(duration, uint.MaxValue));
		}
	}

	/// <summary>The four-character code of the first sample entry in an stsd box (e.g. "djmd", "hvc1").</summary>
	public static string? SampleEntryFormat(Mp4Box stsd)
	{
		var p = stsd.Payload!;
		return p.Length >= 16 ? Encoding.Latin1.GetString(p, 12, 4) : null;
	}

	public static List<(uint Count, uint Delta)> ReadStts(Mp4Box stts)
	{
		Span<byte> p = stts.Payload!.AsSpan();
		var count = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
		List<(uint, uint)> entries = new((int)count);
		for (var i = 0; i < count; i++)
			entries.Add((BinaryPrimitives.ReadUInt32BigEndian(p[(8 + i * 8)..]), BinaryPrimitives.ReadUInt32BigEndian(p[(12 + i * 8)..])));
		return entries;
	}

	public static uint[] ReadSampleSizes(Mp4Box stsz)
	{
		Span<byte> p = stsz.Payload!.AsSpan();
		var uniform = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
		var count = BinaryPrimitives.ReadUInt32BigEndian(p[8..]);
		var sizes = new uint[count];
		for (var i = 0; i < count; i++)
			sizes[i] = uniform != 0 ? uniform : BinaryPrimitives.ReadUInt32BigEndian(p[(12 + i * 4)..]);
		return sizes;
	}

	public static List<(uint FirstChunk, uint SamplesPerChunk)> ReadStsc(Mp4Box stsc)
	{
		Span<byte> p = stsc.Payload!.AsSpan();
		var count = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
		List<(uint, uint)> entries = new((int)count);
		for (var i = 0; i < count; i++)
			entries.Add((BinaryPrimitives.ReadUInt32BigEndian(p[(8 + i * 12)..]), BinaryPrimitives.ReadUInt32BigEndian(p[(12 + i * 12)..])));
		return entries;
	}

	public static ulong[] ReadChunkOffsets(Mp4Box stcoOrCo64)
	{
		Span<byte> p = stcoOrCo64.Payload!.AsSpan();
		var count = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
		var offsets = new ulong[count];
		var is64 = stcoOrCo64.Type == "co64";
		for (var i = 0; i < count; i++)
			offsets[i] = is64 ? BinaryPrimitives.ReadUInt64BigEndian(p[(8 + i * 8)..]) : BinaryPrimitives.ReadUInt32BigEndian(p[(8 + i * 4)..]);
		return offsets;
	}

	public static Mp4Box ChunkOffsetBox(IReadOnlyList<ulong> offsets, bool use64)
	{
		var entrySize = use64 ? 8 : 4;
		var p = new byte[8 + offsets.Count * entrySize];
		BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), (uint)offsets.Count);
		for (var i = 0; i < offsets.Count; i++)
			if (use64) BinaryPrimitives.WriteUInt64BigEndian(p.AsSpan(8 + i * 8), offsets[i]);
			else BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8 + i * 4), (uint)offsets[i]);
		return Mp4Box.Leaf(use64 ? "co64" : "stco", p);
	}

	public static uint[]? ReadSyncSamples(Mp4Box? stss)
	{
		if (stss is null) return null;
		Span<byte> p = stss.Payload!.AsSpan();
		var count = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
		var samples = new uint[count];
		for (var i = 0; i < count; i++) samples[i] = BinaryPrimitives.ReadUInt32BigEndian(p[(8 + i * 4)..]);
		return samples;
	}
}
