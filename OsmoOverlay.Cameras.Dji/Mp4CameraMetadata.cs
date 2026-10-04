using System.Buffers.Binary;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Cameras.Dji;

/// <summary>
///     Copies the camera's own metadata from the source recording(s) into a finished render: the djmd
///     (telemetry: GPS, accelerometer, exposure) and dbgi (camera debug) tracks, plus moov/udta (thumbnails,
///     camera name, shooting settings). ffmpeg can't do this itself - its MP4 muxer rejects these unknown
///     data codecs outright, and the MOV muxer writes them with a wrong sample entry type, so nothing would
///     recognise them afterwards. Done at the box level instead, after ffmpeg has finished writing.
///     The output's moov must be the last top-level box (ffmpeg's default without +faststart): the old moov
///     is replaced by a new mdat holding the copied samples, followed by the rebuilt moov. The video/audio
///     data before it is never touched, so their chunk offsets stay valid. Any failure restores the
///     original moov, leaving the render exactly as ffmpeg wrote it.
/// </summary>
internal static class Mp4CameraMetadata
{
	private static readonly string[] TrackFormats = ["djmd", "dbgi"];

	public static void CopyInto(string outputPath, IReadOnlyList<string> sourcePaths, CameraMetadataSelection selection)
	{
		List<SourceTrack> sourceTracks =
		[
			.. sourcePaths.SelectMany(ReadSourceTracks)
				.Where(t => (t.Format == "djmd" && selection.Telemetry) || (t.Format == "dbgi" && selection.DebugTrack))
		];
		Mp4Box? sourceUdta = selection.ThumbnailsAndInfo ? ReadSourceUdta(sourcePaths[0], selection.SerialNumber) : null;
		if (sourceTracks.Count == 0 && sourceUdta is null) return;

		using var output = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
		List<Mp4TopLevelBox> topLevel = Mp4File.ReadTopLevel(output);
		Mp4TopLevelBox moovBox = topLevel.LastOrDefault(b => b.Type == "moov");
		if (moovBox.Type is null || moovBox.Offset + moovBox.Size != output.Length)
			throw new InvalidDataException("The rendered file's moov box isn't at the end of the file - can't append camera metadata.");

		byte[] originalMoov = new byte[moovBox.Size];
		output.Position = moovBox.Offset;
		output.ReadExactly(originalMoov);
		Mp4Box moov = Mp4File.ReadMoov(output, moovBox);

		try
		{
			Rebuild(output, moov, moovBox.Offset, sourceTracks, sourceUdta, !selection.SerialNumber);
		}
		catch
		{
			output.SetLength(moovBox.Offset);
			output.Position = moovBox.Offset;
			output.Write(originalMoov);
			throw;
		}
	}

	private static void Rebuild(FileStream output, Mp4Box moov, long moovOffset, List<SourceTrack> sourceTracks, Mp4Box? sourceUdta,
		bool maskSerial)
	{
		Mp4Box mvhd = moov.Child("mvhd") ?? throw new InvalidDataException("Rendered file has no mvhd.");
		uint movieTimescale = Mp4Fields.MovieTimescale(mvhd);
		uint nextTrackId = Mp4Fields.NextTrackId(mvhd);

		// Copied samples only up to the rendered video's own length - a frame-limited render is shorter
		// than the source, and a data track running past the video would misrepresent the file.
		double videoSeconds = VideoDurationSeconds(moov);

		output.SetLength(moovOffset);
		output.Position = moovOffset;
		Span<byte> mdatHeader = stackalloc byte[16];
		BinaryPrimitives.WriteUInt32BigEndian(mdatHeader, 1);
		"mdat"u8.CopyTo(mdatHeader[4..]);
		output.Write(mdatHeader);

		foreach (string format in TrackFormats)
		{
			List<SourceTrack> segments = [.. sourceTracks.Where(t => t.Format == format)];
			if (segments.Count == 0) continue;

			if (segments.Any(s => s.Timescale != segments[0].Timescale || !s.Stsd.Payload!.AsSpan().SequenceEqual(segments[0].Stsd.Payload)))
			{
				AppLogger.Warn(string.Format(CoreStrings.Dji_TrackDiffers, format));
				continue;
			}

			Mp4Box trak = BuildTrack(output, segments, nextTrackId, movieTimescale, videoSeconds, maskSerial && format == "djmd");
			moov.Children!.Add(trak);
			nextTrackId++;
		}

		ulong mdatSize = (ulong)(output.Position - moovOffset);
		output.Position = moovOffset + 8;
		Span<byte> sizeBytes = stackalloc byte[8];
		BinaryPrimitives.WriteUInt64BigEndian(sizeBytes, mdatSize);
		output.Write(sizeBytes);
		output.Position = moovOffset + (long)mdatSize;

		Mp4Fields.SetNextTrackId(mvhd, nextTrackId);

		if (sourceUdta is not null)
		{
			// The camera's udta (thumbnails, camera name, shooting settings) replaces ffmpeg's, which only
			// holds its own encoder tag.
			moov.Children!.RemoveAll(c => c.Type == "udta");
			moov.Children.Add(sourceUdta);
		}

		moov.WriteTo(output);
		output.Flush();
	}

	private static Mp4Box BuildTrack(FileStream output, List<SourceTrack> segments, uint trackId, uint movieTimescale, double videoSeconds,
		bool maskSerial)
	{
		uint timescale = segments[0].Timescale;
		ulong maxMediaTime = videoSeconds > 0 ? (ulong)Math.Round(videoSeconds * timescale) : ulong.MaxValue;

		List<ulong> newOffsets = [];
		List<uint> sizes = [];
		List<(uint Count, uint Delta)> stts = [];
		List<uint> syncSamples = [];
		bool anySync = segments.Any(s => s.SyncSamples is not null);
		ulong mediaTime = 0;
		byte[] buffer = new byte[64 * 1024];

		foreach (SourceTrack segment in segments)
		{
			using var source = new FileStream(segment.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
			HashSet<uint>? sync = segment.SyncSamples is null ? null : [.. segment.SyncSamples];
			int sampleIndex = 0;
			foreach ((uint count, uint delta) in segment.Stts)
			{
				for (int i = 0; i < count && sampleIndex < segment.Sizes.Length; i++, sampleIndex++)
				{
					if (mediaTime >= maxMediaTime) goto done;

					uint size = segment.Sizes[sampleIndex];
					newOffsets.Add((ulong)output.Position);
					if (maskSerial) size = CopyDjmdSampleWithoutSerial(source, (long)segment.Offsets[sampleIndex], output, size);
					else CopyBytes(source, (long)segment.Offsets[sampleIndex], output, size, buffer);
					sizes.Add(size);

					if (stts.Count > 0 && stts[^1].Delta == delta) stts[^1] = (stts[^1].Count + 1, delta);
					else stts.Add((1, delta));

					if (anySync && (sync is null || sync.Contains((uint)sampleIndex + 1))) syncSamples.Add((uint)sizes.Count);
					mediaTime += delta;
				}
			}
		}

		done:
		var trak = Mp4Box.Parse("trak", segments[0].Trak.ToBytes().AsSpan(8));
		ulong movieDuration = mediaTime * movieTimescale / timescale;
		trak.Children!.RemoveAll(c => c.Type is "edts" or "tref");
		// Same single "whole track, starting at 0" edit list the camera writes - without one, tools fall
		// back to guessing the track's presentation length from the rest of the file.
		trak.Children.Insert(trak.Children.FindIndex(c => c.Type == "tkhd") + 1, EditList(movieDuration));
		Mp4Fields.SetTrackIdAndDuration(trak.Child("tkhd")!, trackId, movieDuration);
		Mp4Fields.SetMediaDuration(trak.Find("mdia", "mdhd")!, mediaTime);

		Mp4Box stbl = trak.Find("mdia", "minf", "stbl")!;
		stbl.Children!.Clear();
		stbl.Children.Add(segments[0].Stsd);
		stbl.Children.Add(Mp4Box.Leaf("stts", Table(stts.Count, 8, (span, i) =>
		{
			BinaryPrimitives.WriteUInt32BigEndian(span, stts[i].Count);
			BinaryPrimitives.WriteUInt32BigEndian(span[4..], stts[i].Delta);
		})));
		if (anySync)
			stbl.Children.Add(Mp4Box.Leaf("stss", Table(syncSamples.Count, 4, (span, i) => BinaryPrimitives.WriteUInt32BigEndian(span, syncSamples[i]))));
		// One sample per chunk - the copied samples are laid out back to back anyway, and it keeps the
		// chunk table a plain list of per-sample offsets.
		stbl.Children.Add(Mp4Box.Leaf("stsc", Table(1, 12, (span, _) =>
		{
			BinaryPrimitives.WriteUInt32BigEndian(span, 1);
			BinaryPrimitives.WriteUInt32BigEndian(span[4..], 1);
			BinaryPrimitives.WriteUInt32BigEndian(span[8..], 1);
		})));
		byte[] stsz = new byte[12 + sizes.Count * 4];
		BinaryPrimitives.WriteUInt32BigEndian(stsz.AsSpan(8), (uint)sizes.Count);
		for (int i = 0; i < sizes.Count; i++) BinaryPrimitives.WriteUInt32BigEndian(stsz.AsSpan(12 + i * 4), sizes[i]);
		stbl.Children.Add(Mp4Box.Leaf("stsz", stsz));
		stbl.Children.Add(Mp4Fields.ChunkOffsetBox(newOffsets, true));
		return trak;
	}

	private static Mp4Box EditList(ulong movieDuration)
	{
		bool use64 = movieDuration > uint.MaxValue;
		byte[] elst = new byte[use64 ? 28 : 20];
		elst[0] = (byte)(use64 ? 1 : 0);
		BinaryPrimitives.WriteUInt32BigEndian(elst.AsSpan(4), 1);
		if (use64)
		{
			BinaryPrimitives.WriteUInt64BigEndian(elst.AsSpan(8), movieDuration);
			BinaryPrimitives.WriteInt64BigEndian(elst.AsSpan(16), 0);
			BinaryPrimitives.WriteUInt32BigEndian(elst.AsSpan(24), 0x00010000);
		}
		else
		{
			BinaryPrimitives.WriteUInt32BigEndian(elst.AsSpan(8), (uint)movieDuration);
			BinaryPrimitives.WriteInt32BigEndian(elst.AsSpan(12), 0);
			BinaryPrimitives.WriteUInt32BigEndian(elst.AsSpan(16), 0x00010000);
		}

		return Mp4Box.Parse("edts", Mp4Box.Leaf("elst", elst).ToBytes());
	}

	private static byte[] Table(int count, int entrySize, SpanAction write)
	{
		byte[] payload = new byte[8 + count * entrySize];
		BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)count);
		for (int i = 0; i < count; i++) write(payload.AsSpan(8 + i * entrySize, entrySize), i);
		return payload;
	}

	private delegate void SpanAction(Span<byte> span, int index);

	private static void CopyBytes(FileStream source, long offset, Stream destination, uint size, byte[] buffer)
	{
		source.Position = offset;
		long remaining = size;
		while (remaining > 0)
		{
			int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
			if (read == 0) throw new EndOfStreamException("Source file ended inside a camera metadata sample.");
			destination.Write(buffer, 0, read);
			remaining -= read;
		}
	}

	// Protobuf path of the camera's serial number inside a djmd sample - verified on Osmo Action 6 files:
	// a string in the per-file header message (f1.1, next to the firmware version at f1.1.6), and the only
	// place in the whole file the serial occurs.
	private static readonly int[] SerialNumberPath = [1, 1, 5];

	/// <summary>
	///     Copies one djmd sample with the serial number field removed outright - not blanked, the field
	///     and its value are gone. The enclosing messages' length prefixes are re-encoded to match, so the
	///     result is still valid protobuf (just a field shorter); returns the new sample size. The telemetry
	///     fields DjiMetaTelemetryParser reads (f2/f4) sit outside f1 and come through byte for byte.
	/// </summary>
	private static uint CopyDjmdSampleWithoutSerial(FileStream source, long offset, Stream destination, uint size)
	{
		byte[] sample = new byte[size];
		source.Position = offset;
		source.ReadExactly(sample);
		byte[] cleaned = RemoveField(sample, SerialNumberPath);
		destination.Write(cleaned);
		return (uint)cleaned.Length;
	}

	/// <summary>`message` without the length-delimited field at `path` (field numbers, outermost first) - unchanged if there's none.</summary>
	private static byte[] RemoveField(byte[] message, ReadOnlySpan<int> path)
	{
		if (FindField(message, path[0]) is not { } field) return message;

		if (path.Length == 1)
			return [.. message.AsSpan(0, field.HeaderStart), .. message.AsSpan(field.DataEnd)];

		byte[] inner = message[field.DataStart..field.DataEnd];
		byte[] newInner = RemoveField(inner, path[1..]);
		if (ReferenceEquals(newInner, inner)) return message;

		return
		[
			.. message.AsSpan(0, field.HeaderStart),
			.. message.AsSpan(field.HeaderStart, field.TagEnd - field.HeaderStart),
			.. EncodeVarint((ulong)newInner.Length),
			.. newInner,
			.. message.AsSpan(field.DataEnd)
		];
	}

	/// <summary>
	///     Where the first length-delimited field `fieldNumber` sits in a protobuf message: HeaderStart is its
	///     tag, TagEnd where the tag ends and the length prefix begins, [DataStart, DataEnd) its payload.
	/// </summary>
	private static (int HeaderStart, int TagEnd, int DataStart, int DataEnd)? FindField(byte[] data, int fieldNumber)
	{
		int pos = 0;
		while (pos < data.Length)
		{
			int headerStart = pos;
			if (!TryReadVarint(data, ref pos, data.Length, out ulong tag)) return null;
			int tagEnd = pos;
			switch ((int)(tag & 7))
			{
				case 0:
					if (!TryReadVarint(data, ref pos, data.Length, out _)) return null;
					break;
				case 1:
					pos += 8;
					break;
				case 5:
					pos += 4;
					break;
				case 2:
					if (!TryReadVarint(data, ref pos, data.Length, out ulong length) || length > (ulong)(data.Length - pos)) return null;
					if ((int)(tag >> 3) == fieldNumber) return (headerStart, tagEnd, pos, pos + (int)length);
					pos += (int)length;
					break;
				default:
					return null;
			}
		}

		return null;
	}

	private static byte[] EncodeVarint(ulong value)
	{
		List<byte> bytes = [];
		do
		{
			byte b = (byte)(value & 0x7F);
			value >>= 7;
			bytes.Add(value != 0 ? (byte)(b | 0x80) : b);
		} while (value != 0);

		return [.. bytes];
	}

	private static bool TryReadVarint(byte[] data, ref int pos, int end, out ulong value)
	{
		value = 0;
		for (int shift = 0; pos < end && shift < 64; shift += 7)
		{
			byte b = data[pos++];
			value |= (ulong)(b & 0x7F) << shift;
			if ((b & 0x80) == 0) return true;
		}

		return false;
	}

	private static double VideoDurationSeconds(Mp4Box moov)
	{
		foreach (Mp4Box trak in moov.Children!.Where(c => c.Type == "trak"))
		{
			if (trak.Find("mdia", "hdlr")?.Payload is not { Length: >= 12 } hdlr || !hdlr.AsSpan(8, 4).SequenceEqual("vide"u8)) continue;
			(uint timescale, ulong duration) = Mp4Fields.MediaTiming(trak.Find("mdia", "mdhd")!);
			return timescale > 0 ? duration / (double)timescale : 0;
		}

		return 0;
	}

	internal static IEnumerable<SourceTrack> ReadSourceTracks(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		Mp4TopLevelBox moovBox = Mp4File.ReadTopLevel(stream).FirstOrDefault(b => b.Type == "moov");
		if (moovBox.Type is null) return [];

		Mp4Box moov = Mp4File.ReadMoov(stream, moovBox);
		List<SourceTrack> tracks = [];
		foreach (Mp4Box trak in moov.Children!.Where(c => c.Type == "trak"))
		{
			Mp4Box? stbl = trak.Find("mdia", "minf", "stbl");
			Mp4Box? stsd = stbl?.Child("stsd");
			if (stbl is null || stsd is null || Mp4Fields.SampleEntryFormat(stsd) is not { } format || !TrackFormats.Contains(format)) continue;

			uint[] sizes = Mp4Fields.ReadSampleSizes(stbl.Child("stsz")!);
			ulong[] chunkOffsets = Mp4Fields.ReadChunkOffsets(stbl.Child("stco") ?? stbl.Child("co64")!);
			tracks.Add(new SourceTrack(path, format, trak, stsd, Mp4Fields.MediaTiming(trak.Find("mdia", "mdhd")!).Timescale,
				Mp4Fields.ReadStts(stbl.Child("stts")!), sizes,
				SampleOffsets(Mp4Fields.ReadStsc(stbl.Child("stsc")!), chunkOffsets, sizes),
				Mp4Fields.ReadSyncSamples(stbl.Child("stss"))));
		}

		return tracks;
	}

	/// <summary>Absolute file offset of every sample, from the chunk table (stco/co64 + stsc + stsz).</summary>
	private static ulong[] SampleOffsets(List<(uint FirstChunk, uint SamplesPerChunk)> stsc, ulong[] chunkOffsets, uint[] sizes)
	{
		ulong[] offsets = new ulong[sizes.Length];
		int sample = 0;
		for (int chunk = 0; chunk < chunkOffsets.Length && sample < sizes.Length; chunk++)
		{
			int entry = stsc.FindLastIndex(e => e.FirstChunk <= chunk + 1);
			uint perChunk = entry >= 0 ? stsc[entry].SamplesPerChunk : 1;
			ulong offset = chunkOffsets[chunk];
			for (int i = 0; i < perChunk && sample < sizes.Length; i++, sample++)
			{
				offsets[sample] = offset;
				offset += sizes[sample];
			}
		}

		if (sample < sizes.Length) throw new InvalidDataException("Camera metadata track's chunk table covers fewer samples than its size table.");
		return offsets;
	}

	/// <summary>
	///     The source's moov/udta (thumbnails, camera name, shooting settings, original file path). Without
	///     `keepDeviceId`, its "©uid" box - a 4-byte identifier of unknown scope, possibly per device - is
	///     left out, treated like the serial number since there's no way to tell it isn't one.
	/// </summary>
	private static Mp4Box? ReadSourceUdta(string path, bool keepDeviceId)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		Mp4TopLevelBox moovBox = Mp4File.ReadTopLevel(stream).FirstOrDefault(b => b.Type == "moov");
		if (moovBox.Type is null || Mp4File.ReadMoov(stream, moovBox).Child("udta") is not { Payload: { } payload }) return null;
		if (keepDeviceId) return Mp4Box.Leaf("udta", payload);

		using var filtered = new MemoryStream();
		foreach (Mp4Box child in Mp4Box.ParseChildren(payload).Where(c => c.Type != "\u00A9uid"))
			child.WriteTo(filtered);
		return Mp4Box.Leaf("udta", filtered.ToArray());
	}

	internal sealed record SourceTrack(
		string Path,
		string Format,
		Mp4Box Trak,
		Mp4Box Stsd,
		uint Timescale,
		List<(uint Count, uint Delta)> Stts,
		uint[] Sizes,
		ulong[] Offsets,
		uint[]? SyncSamples);
}
