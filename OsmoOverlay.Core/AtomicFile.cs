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
		var tempPath = TempPathFor(path);
		try
		{
			File.WriteAllText(tempPath, contents);
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
		var tempPath = TempPathFor(path);
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
