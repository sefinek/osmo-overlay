namespace OsmoOverlay.Core;

/// <summary>
///     Write-then-rename so a crash or power loss mid-write can never leave a truncated/corrupt file at
///     `path` - only ever an orphaned .tmp file next to it, since the temp file lives in the same
///     directory (so the final File.Move is a same-volume rename, atomic on the file systems this app
///     targets). Shared by FileSummaryCache, OverlayPresetStore, OverlaySettingsStore and
///     MapTileFetcher - a corrupt preset or settings file isn't recomputable like the telemetry/tile
///     caches are, so this guarantee matters even more for those.
/// </summary>
internal static class AtomicFile
{
	public static void WriteAllText(string path, string contents)
	{
		Write(path, stream =>
		{
			using var writer = new StreamWriter(stream);
			writer.Write(contents);
		});
	}

	/// <summary>The file's contents written straight into the temp file - for one too big to build in memory first.</summary>
	public static void Write(string path, Action<Stream> write)
	{
		string tempPath = TempPathFor(path);
		try
		{
			using (FileStream stream = File.Create(tempPath))
				write(stream);
			File.Move(tempPath, path, true);
		}
		catch
		{
			TryDelete(tempPath);
			throw;
		}
	}

	public static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)
	{
		string tempPath = TempPathFor(path);
		try
		{
			await File.WriteAllBytesAsync(tempPath, bytes, ct);
			File.Move(tempPath, path, true);
		}
		catch
		{
			TryDelete(tempPath);
			throw;
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch
		{
			// Best-effort: a leftover .tmp is harmless, the original exception is what matters.
		}
	}

	private static string TempPathFor(string path)
	{
		return $"{path}.{Guid.NewGuid():N}.tmp";
	}
}
