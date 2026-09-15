using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core;

internal static class FileSummaryCache
{
	// Bump whenever telemetry extraction or derivation logic changes, so stale cache
	// entries computed with the old logic are treated as a cache miss automatically.
	public const int FormatVersion = 11;

	private static readonly string CacheDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "cache");

	public static FileSummary? TryLoad(IReadOnlyList<string> inputPaths)
	{
		var cachePath = GetCachePath(inputPaths);
		if (!File.Exists(cachePath)) return null;

		try
		{
			var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(cachePath));
			if (entry is null || entry.FormatVersion != FormatVersion) return null;
			if (entry.Files.Count != inputPaths.Count) return null;

			for (var i = 0; i < inputPaths.Count; i++)
			{
				var info = new FileInfo(inputPaths[i]);
				if (entry.Files[i].FileSizeBytes != info.Length ||
				    entry.Files[i].LastWriteTimeUtcTicks != info.LastWriteTimeUtc.Ticks)
					return null;
			}

			return entry.Summary;
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, $"Cache entry for {string.Join(", ", inputPaths)} is corrupt or unreadable, treating as a cache miss");
			return null;
		}
	}

	public static int ClearAll()
	{
		if (!Directory.Exists(CacheDir)) return 0;

		var files = Directory.GetFiles(CacheDir, "*.json");
		var deleted = 0;
		foreach (var file in files)
			try
			{
				File.Delete(file);
				deleted++;
			}
			catch
			{
				// Best-effort: a file in use or otherwise undeletable is skipped, not fatal.
			}

		return deleted;
	}

	public static void Save(IReadOnlyList<string> inputPaths, FileSummary summary)
	{
		try
		{
			Directory.CreateDirectory(CacheDir);
			List<FileStamp> files = inputPaths.Select(path =>
			{
				var info = new FileInfo(path);
				return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
			}).ToList();

			var entry = new CacheEntry(FormatVersion, files, summary);
			File.WriteAllText(GetCachePath(inputPaths), JsonSerializer.Serialize(entry));
		}
		catch (Exception ex)
		{
			// Best-effort cache: a failed write should not break the summary flow.
			AppLogger.Warn(ex, $"Failed to write cache entry for {string.Join(", ", inputPaths)}");
		}
	}

	public static string GetCachePath(IReadOnlyList<string> inputPaths)
	{
		var joined = string.Join('|', inputPaths.Select(p => Path.GetFullPath(p).ToLowerInvariant()));
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
		return Path.Combine(CacheDir, $"{hash}.json");
	}

	private sealed record FileStamp(long FileSizeBytes, long LastWriteTimeUtcTicks);

	private sealed record CacheEntry(int FormatVersion, List<FileStamp> Files, FileSummary Summary);
}
