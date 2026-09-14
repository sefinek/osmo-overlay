using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OsmoOverlay.Core;

internal static class FileSummaryCache
{
	// Bump whenever telemetry extraction or derivation logic changes, so stale cache
	// entries computed with the old logic are treated as a cache miss automatically.
	public const int FormatVersion = 9;

	private static readonly string CacheDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "cache");

	public static FileSummary? TryLoad(string inputPath)
	{
		var cachePath = GetCachePath(inputPath);
		if (!File.Exists(cachePath)) return null;

		try
		{
			var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(cachePath));
			if (entry is null || entry.FormatVersion != FormatVersion) return null;

			var info = new FileInfo(inputPath);
			if (entry.FileSizeBytes != info.Length || entry.LastWriteTimeUtcTicks != info.LastWriteTimeUtc.Ticks)
				return null;

			return entry.Summary;
		}
		catch
		{
			return null;
		}
	}

	public static void Save(string inputPath, FileSummary summary)
	{
		try
		{
			Directory.CreateDirectory(CacheDir);
			var info = new FileInfo(inputPath);
			var entry = new CacheEntry(FormatVersion, info.Length, info.LastWriteTimeUtc.Ticks, summary);
			File.WriteAllText(GetCachePath(inputPath), JsonSerializer.Serialize(entry));
		}
		catch
		{
			// Best-effort cache: a failed write should not break the summary flow.
		}
	}

	public static string GetCachePath(string inputPath)
	{
		var fullPath = Path.GetFullPath(inputPath).ToLowerInvariant();
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)));
		return Path.Combine(CacheDir, $"{hash}.json");
	}

	private sealed record CacheEntry(int FormatVersion, long FileSizeBytes, long LastWriteTimeUtcTicks, FileSummary Summary);
}
