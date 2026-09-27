namespace OsmoOverlay.Core.Ffmpeg;

/// <summary>
///     Moves moov in front of the media data ("fast start"), so a browser/messenger can start playing the
///     file before it has downloaded all of it - what ffmpeg's -movflags +faststart does, redone here for
///     a file a camera format edited after ffmpeg finished (ICameraFormat.CopyMetadata). Every chunk offset shifts by the size of
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
