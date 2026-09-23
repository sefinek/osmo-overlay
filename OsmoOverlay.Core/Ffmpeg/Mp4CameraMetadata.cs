using System.Buffers.Binary;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Ffmpeg;

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

		var originalMoov = new byte[moovBox.Size];
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
		var movieTimescale = Mp4Fields.MovieTimescale(mvhd);
		var nextTrackId = Mp4Fields.NextTrackId(mvhd);

		// Copied samples only up to the rendered video's own length - a frame-limited render is shorter
		// than the source, and a data track running past the video would misrepresent the file.
		var videoSeconds = VideoDurationSeconds(moov);

		output.SetLength(moovOffset);
		output.Position = moovOffset;
		Span<byte> mdatHeader = stackalloc byte[16];
		BinaryPrimitives.WriteUInt32BigEndian(mdatHeader, 1);
		"mdat"u8.CopyTo(mdatHeader[4..]);
		output.Write(mdatHeader);

		foreach (var format in TrackFormats)
		{
			List<SourceTrack> segments = [.. sourceTracks.Where(t => t.Format == format)];
			if (segments.Count == 0) continue;

			if (segments.Any(s => s.Timescale != segments[0].Timescale || !s.Stsd.Payload!.AsSpan().SequenceEqual(segments[0].Stsd.Payload)))
			{
				AppLogger.Warn($"Camera '{format}' track differs between segments (timescale/sample description) - not copied.");
				continue;
			}

			Mp4Box trak = BuildTrack(output, segments, nextTrackId, movieTimescale, videoSeconds, maskSerial && format == "djmd");
			moov.Children!.Add(trak);
			nextTrackId++;
		}

		var mdatSize = (ulong)(output.Position - moovOffset);
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
		var timescale = segments[0].Timescale;
		var maxMediaTime = videoSeconds > 0 ? (ulong)Math.Round(videoSeconds * timescale) : ulong.MaxValue;

		List<ulong> newOffsets = [];
		List<uint> sizes = [];
		List<(uint Count, uint Delta)> stts = [];
		List<uint> syncSamples = [];
		var anySync = segments.Any(s => s.SyncSamples is not null);
		ulong mediaTime = 0;
		var buffer = new byte[64 * 1024];

		foreach (SourceTrack segment in segments)
		{
			using var source = new FileStream(segment.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
			HashSet<uint>? sync = segment.SyncSamples is null ? null : [.. segment.SyncSamples];
			var sampleIndex = 0;
			foreach (var (count, delta) in segment.Stts)
				for (var i = 0; i < count && sampleIndex < segment.Sizes.Length; i++, sampleIndex++)
				{
					if (mediaTime >= maxMediaTime) goto done;

					var size = segment.Sizes[sampleIndex];
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

		done:
		Mp4Box trak = Mp4Box.Parse("trak", segments[0].Trak.ToBytes().AsSpan(8));
		var movieDuration = mediaTime * movieTimescale / timescale;
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
		var stsz = new byte[12 + sizes.Count * 4];
		BinaryPrimitives.WriteUInt32BigEndian(stsz.AsSpan(8), (uint)sizes.Count);
		for (var i = 0; i < sizes.Count; i++) BinaryPrimitives.WriteUInt32BigEndian(stsz.AsSpan(12 + i * 4), sizes[i]);
		stbl.Children.Add(Mp4Box.Leaf("stsz", stsz));
		stbl.Children.Add(Mp4Fields.ChunkOffsetBox(newOffsets, true));
		return trak;
	}

	private static Mp4Box EditList(ulong movieDuration)
	{
		var use64 = movieDuration > uint.MaxValue;
		var elst = new byte[use64 ? 28 : 20];
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
		var payload = new byte[8 + count * entrySize];
		BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)count);
		for (var i = 0; i < count; i++) write(payload.AsSpan(8 + i * entrySize, entrySize), i);
		return payload;
	}

	private delegate void SpanAction(Span<byte> span, int index);

	private static void CopyBytes(FileStream source, long offset, Stream destination, uint size, byte[] buffer)
	{
		source.Position = offset;
		var remaining = (long)size;
		while (remaining > 0)
		{
			var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
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
		var sample = new byte[size];
		source.Position = offset;
		source.ReadExactly(sample);
		var cleaned = RemoveField(sample, SerialNumberPath);
		destination.Write(cleaned);
		return (uint)cleaned.Length;
	}

	/// <summary>`message` without the length-delimited field at `path` (field numbers, outermost first) - unchanged if there's none.</summary>
	private static byte[] RemoveField(byte[] message, ReadOnlySpan<int> path)
	{
		if (FindField(message, path[0]) is not { } field) return message;

		if (path.Length == 1)
			return [.. message.AsSpan(0, field.HeaderStart), .. message.AsSpan(field.DataEnd)];

		var inner = message[field.DataStart..field.DataEnd];
		var newInner = RemoveField(inner, path[1..]);
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
		var pos = 0;
		while (pos < data.Length)
		{
			var headerStart = pos;
			if (!TryReadVarint(data, ref pos, data.Length, out var tag)) return null;
			var tagEnd = pos;
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
					if (!TryReadVarint(data, ref pos, data.Length, out var length) || length > (ulong)(data.Length - pos)) return null;
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
			var b = (byte)(value & 0x7F);
			value >>= 7;
			bytes.Add(value != 0 ? (byte)(b | 0x80) : b);
		} while (value != 0);

		return [.. bytes];
	}

	private static bool TryReadVarint(byte[] data, ref int pos, int end, out ulong value)
	{
		value = 0;
		for (var shift = 0; pos < end && shift < 64; shift += 7)
		{
			var b = data[pos++];
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
			var (timescale, duration) = Mp4Fields.MediaTiming(trak.Find("mdia", "mdhd")!);
			return timescale > 0 ? duration / (double)timescale : 0;
		}

		return 0;
	}

	private static IEnumerable<SourceTrack> ReadSourceTracks(string path)
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

			var sizes = Mp4Fields.ReadSampleSizes(stbl.Child("stsz")!);
			var chunkOffsets = Mp4Fields.ReadChunkOffsets(stbl.Child("stco") ?? stbl.Child("co64")!);
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
		var offsets = new ulong[sizes.Length];
		var sample = 0;
		for (var chunk = 0; chunk < chunkOffsets.Length && sample < sizes.Length; chunk++)
		{
			var entry = stsc.FindLastIndex(e => e.FirstChunk <= chunk + 1);
			var perChunk = entry >= 0 ? stsc[entry].SamplesPerChunk : 1;
			var offset = chunkOffsets[chunk];
			for (var i = 0; i < perChunk && sample < sizes.Length; i++, sample++)
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

	private sealed record SourceTrack(
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

/// <summary>
///     Moves moov in front of the media data ("fast start"), so a browser/messenger can start playing the
///     file before it has downloaded all of it - what ffmpeg's -movflags +faststart does, redone here for
///     a file Mp4CameraMetadata edited after ffmpeg finished. Every chunk offset shifts by the size of
///     the moved moov; a 32-bit stco table that would overflow is upgraded to co64 first. Written to a
///     temporary file next to the output and swapped in only once complete.
/// </summary>
internal static class Mp4FastStart
{
	public static void Apply(string path)
	{
		var tempPath = $"{path}.{Guid.NewGuid():N}.faststart.tmp";
		try
		{
			using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
			{
				List<Mp4TopLevelBox> topLevel = Mp4File.ReadTopLevel(input);
				var moovIndex = topLevel.FindIndex(b => b.Type == "moov");
				var firstMdat = topLevel.FindIndex(b => b.Type == "mdat");
				if (moovIndex < 0 || firstMdat < 0 || moovIndex < firstMdat) return;

				Mp4Box moov = Mp4File.ReadMoov(input, topLevel[moovIndex]);
				List<(Mp4Box Stbl, ulong[] Offsets)> tables = [];
				foreach (Mp4Box trak in moov.Children!.Where(c => c.Type == "trak"))
				{
					Mp4Box? stbl = trak.Find("mdia", "minf", "stbl");
					Mp4Box? chunkBox = stbl?.Child("stco") ?? stbl?.Child("co64");
					if (stbl is not null && chunkBox is not null) tables.Add((stbl, Mp4Fields.ReadChunkOffsets(chunkBox)));
				}

				// Converting a table to co64 grows moov, which grows the shift - so settle the size first.
				var force64 = false;
				ulong shift;
				while (true)
				{
					foreach ((Mp4Box stbl, var offsets) in tables) ReplaceChunkOffsets(stbl, offsets, 0, force64);
					shift = (ulong)moov.Size;
					if (force64 || tables.All(t => t.Offsets.Length == 0 || t.Offsets.Max() + shift <= uint.MaxValue)) break;
					force64 = true;
				}

				foreach ((Mp4Box stbl, var offsets) in tables) ReplaceChunkOffsets(stbl, offsets, shift, force64);

				using var outputFile = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
				var insertAt = topLevel[firstMdat].Offset;
				CopyRange(input, 0, insertAt, outputFile);
				moov.WriteTo(outputFile);
				foreach (Mp4TopLevelBox box in topLevel.Where((b, i) => i >= firstMdat && i != moovIndex))
					CopyRange(input, box.Offset, box.Size, outputFile);
			}

			File.Move(tempPath, path, true);
		}
		finally
		{
			if (File.Exists(tempPath)) File.Delete(tempPath);
		}
	}

	private static void ReplaceChunkOffsets(Mp4Box stbl, ulong[] offsets, ulong shift, bool force64)
	{
		var index = stbl.Children!.FindIndex(c => c.Type is "stco" or "co64");
		var use64 = force64 || stbl.Children[index].Type == "co64";
		stbl.Children[index] = Mp4Fields.ChunkOffsetBox([.. offsets.Select(o => o + shift)], use64);
	}

	private static void CopyRange(Stream input, long offset, long length, Stream output)
	{
		var buffer = new byte[1 << 20];
		input.Position = offset;
		while (length > 0)
		{
			var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
			if (read == 0) throw new EndOfStreamException();
			output.Write(buffer, 0, read);
			length -= read;
		}
	}
}

/// <summary>
///     Which parts of the camera's metadata Mp4CameraMetadata carries over. SerialNumber also covers the
///     udta device-id box, and only matters for what's otherwise kept (the serial lives in the telemetry
///     track, the device id in the thumbnails/info block).
/// </summary>
public sealed record CameraMetadataSelection(bool Telemetry, bool DebugTrack, bool ThumbnailsAndInfo, bool SerialNumber)
{
	public bool Any => Telemetry || DebugTrack || ThumbnailsAndInfo;
}
