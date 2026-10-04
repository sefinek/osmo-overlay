using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Preview;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

internal static class FileSummaryCache
{
	// Bump whenever telemetry extraction or derivation logic changes, so stale cache
	// entries computed with the old logic are treated as a cache miss automatically.
	public const int FormatVersion = 29;

	private const string CompressedSuffix = ".br";

	private static readonly string CacheDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "cache");

	/// <summary>
	///     Summary is null on any kind of miss (no file, corrupt, stamps don't match, stale format).
	///     StaleFormatVersion (and StaleSizeBytes, the deleted file's size) is set only for that last case - a cache entry that otherwise matches this
	///     exact file (same path/size/mtime) but was written by an older FormatVersion - so callers can
	///     tell "never analyzed before" apart from "analyzed before, but the logic has changed since" and
	///     report that distinctly instead of a generic cache miss. A stale entry is deleted right here,
	///     before the (slow) recompute starts, so a recompute that then fails can't leave it behind.
	/// </summary>
	public static (FileSummary? Summary, int? StaleFormatVersion, long StaleSizeBytes) TryLoad(IReadOnlyList<string> inputPaths)
	{
		string cachePath = GetCachePath(inputPaths);
		// An entry from before entries were compressed (plain JSON) - read like any other, then dropped as stale.
		bool legacy = !File.Exists(cachePath) && File.Exists(LegacyPath(cachePath));
		string path = legacy ? LegacyPath(cachePath) : cachePath;
		if (!File.Exists(path)) return (null, null, 0);

		try
		{
			if (ReadHeader(path, !legacy) is not { } header || header.Files.Count != inputPaths.Count) return (null, null, 0);

			for (int i = 0; i < inputPaths.Count; i++)
			{
				var info = new FileInfo(inputPaths[i]);
				if (!info.Exists ||
				    header.Files[i].FileSizeBytes != info.Length ||
				    header.Files[i].LastWriteTimeUtcTicks != info.LastWriteTimeUtc.Ticks)
					return (null, null, 0);
			}

			if (legacy || header.FormatVersion != FormatVersion)
			{
				long size = new FileInfo(path).Length;
				TryDelete(path);
				return (null, header.FormatVersion, size);
			}

			CacheEntry? entry;
			using (Stream stream = OpenEntry(path, true))
				entry = JsonSerializer.Deserialize<CacheEntry>(stream);
			if (entry is null) return (null, null, 0);

			FileSummary summary = Reinflate(entry);
			MarkUsed(path);
			return (summary, null, 0);
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, string.Format(CoreStrings.Cache_Unreadable, string.Join(", ", inputPaths)));
			return (null, null, 0);
		}
	}

	public static int ClearAll()
	{
		return Delete(CacheFiles(CacheDir));
	}

	/// <summary>
	///     An entry streamed, Brotli-compressed: its JSON is mostly the frames' numbers - an hour at 59.94 fps took 212 MB, compressed
	///     35 MB - and still read the way it's written, so a new field in a frame needs nothing here.
	///     Read streaming, never into one string.
	/// </summary>
	private static Stream OpenEntry(string path, bool compressed)
	{
		FileStream file = File.OpenRead(path);
		return compressed ? new BrotliStream(file, CompressionMode.Decompress) : file;
	}

	/// <summary>
	///     The entry's version and file stamps, from its start (CacheEntry's first two properties) - whether it's this
	///     recording's and current, without reading and deserializing the rest of it; an older version may not even fit
	///     today's types. Null when the start isn't laid out that way.
	/// </summary>
	private static (int FormatVersion, List<FileStamp> Files)? ReadHeader(string path, bool compressed)
	{
		byte[] buffer = new byte[64 * 1024];
		int length;
		using (Stream stream = OpenEntry(path, compressed))
			length = stream.ReadAtLeast(buffer, buffer.Length, false);

		var reader = new Utf8JsonReader(buffer.AsSpan(0, length), length < buffer.Length, default);
		if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

		int? version = null;
		List<FileStamp>? files = null;
		while ((version is null || files is null) && reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
		{
			if (reader.ValueTextEquals(nameof(CacheEntry.FormatVersion)) && reader.Read() && reader.TokenType == JsonTokenType.Number)
				version = reader.GetInt32();
			else if (reader.ValueTextEquals(nameof(CacheEntry.Files)) && reader.Read())
				files = JsonSerializer.Deserialize<List<FileStamp>>(ref reader);
			else
				return null;
		}

		return version is { } v && files is not null ? (v, files) : null;
	}

	private static string LegacyPath(string cachePath)
	{
		return cachePath[..^CompressedSuffix.Length];
	}

	/// <summary>
	///     Deletes every cache file (summaries, waveforms, leftover temp files) not used for `unusedFor`: an entry's
	///     modification time is when it was last written or read (MarkUsed). A temp file still being written is new, so it stays.
	/// </summary>
	public static int DeleteUnused(TimeSpan unusedFor, string? directory = null)
	{
		DateTime cutoff = DateTime.UtcNow - unusedFor;
		int deleted = Delete(CacheFiles(directory ?? CacheDir).Where(file => File.GetLastWriteTimeUtc(file) < cutoff));
		if (deleted > 0) AppLogger.Info($"Cache cleanup: deleted {deleted} file(s) unused for {unusedFor.TotalDays:0} days");
		return deleted;
	}

	/// <summary>An entry just read counts as used - best-effort, a read-only cache only means it may be cleaned up earlier.</summary>
	internal static void MarkUsed(string path)
	{
		try
		{
			File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLogger.Info($"Cache entry not marked as used ({path}): {ex.Message}");
		}
	}

	private static IEnumerable<string> CacheFiles(string directory)
	{
		if (!Directory.Exists(directory)) return [];

		// The timeline's waveforms (WaveformCache) live alongside and go with the rest.
		return Directory.GetFiles(directory, "*.json" + CompressedSuffix).Concat(Directory.GetFiles(directory, "*.json"))
			.Concat(Directory.GetFiles(directory, "*.tmp")).Concat(WaveformCache.Files(directory)).Distinct();
	}

	private static int Delete(IEnumerable<string> files)
	{
		int deleted = 0;
		foreach (string file in files)
		{
			try
			{
				File.Delete(file);
				deleted++;
			}
			catch
			{
				// Best-effort: a file in use or otherwise undeletable is skipped, not fatal.
			}
		}

		return deleted;
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception ex)
		{
			// Save overwrites it anyway once the recompute finishes.
			AppLogger.Warn(ex, string.Format(CoreStrings.Cache_DeleteFailed, path));
		}
	}

	/// <summary>False when the write failed - logged here, never thrown, since the cache is best-effort.</summary>
	public static bool Save(IReadOnlyList<string> inputPaths, FileSummary summary)
	{
		try
		{
			Directory.CreateDirectory(CacheDir);
			var files = inputPaths.Select(path =>
			{
				var info = new FileInfo(path);
				return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
			}).ToList();

			// DerivedFrame.Raw is a full copy of the matching TelemetryFrames entry (TelemetryProcessor
			// builds each DerivedFrame from exactly one TelemetryFrame, 1:1 by index) - serializing it
			// verbatim would store every raw sample twice, roughly doubling the file for a telemetry-
			// heavy recording. Strip it here and pair DerivedExtras back up with TelemetryFrames by
			// index in Reinflate instead.
			var derivedExtras = summary.DerivedFrames?.Select(CachedDerivedFrame.From).ToList();
			var entry = new CacheEntry(FormatVersion, files, summary with { DerivedFrames = null }, derivedExtras);
			string cachePath = GetCachePath(inputPaths);
			AtomicFile.Write(cachePath, stream =>
			{
				using var compressed = new BrotliStream(stream, CompressionLevel.Fastest, true);
				JsonSerializer.Serialize(compressed, entry);
			});
			if (File.Exists(LegacyPath(cachePath))) TryDelete(LegacyPath(cachePath));
			return true;
		}
		catch (Exception ex)
		{
			// Best-effort cache: a failed write should not break the summary flow.
			AppLogger.Warn(ex, string.Format(CoreStrings.Cache_WriteFailed, string.Join(", ", inputPaths)));
			return false;
		}
	}

	public static string GetCachePath(IReadOnlyList<string> inputPaths)
	{
		string joined = string.Join('|', inputPaths.Select(p => Path.GetFullPath(p).ToLowerInvariant()));
		string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
		return Path.Combine(CacheDir, $"{hash}.json{CompressedSuffix}");
	}

	/// <summary>
	///     Rebuilds Summary.DerivedFrames from DerivedExtras + Summary.TelemetryFrames (see Save) - no
	///     DerivedExtras means the recording had no derived telemetry. Treats a count mismatch as a corrupt
	///     entry (caught by TryLoad, same as any other unreadable cache file) rather than reinflating out
	///     of bounds.
	/// </summary>
	private static FileSummary Reinflate(CacheEntry entry)
	{
		if (entry.DerivedExtras is null) return entry.Summary;

		IReadOnlyList<TelemetryFrame> raw = entry.Summary.TelemetryFrames
		                                    ?? throw new InvalidDataException("Cache entry has DerivedExtras but no TelemetryFrames to pair them with.");
		if (raw.Count != entry.DerivedExtras.Count)
			throw new InvalidDataException("Cache entry's DerivedExtras count doesn't match TelemetryFrames.");

		var derivedFrames = new List<DerivedFrame>(raw.Count);
		for (int i = 0; i < raw.Count; i++)
			derivedFrames.Add(entry.DerivedExtras[i].ToDerivedFrame(raw[i]));

		return entry.Summary with { DerivedFrames = derivedFrames };
	}

	private sealed record FileStamp(long FileSizeBytes, long LastWriteTimeUtcTicks);

	private sealed record CacheEntry(int FormatVersion, List<FileStamp> Files, FileSummary Summary, List<CachedDerivedFrame>? DerivedExtras);

	/// <summary>DerivedFrame minus Raw (see Save) - everything TelemetryProcessor computes from one TelemetryFrame, paired back up with it by index in Reinflate.</summary>
	private sealed record CachedDerivedFrame(
		double SpeedKmh,
		double HeadingDegrees,
		double GradientPercent,
		double CumulativeDistanceMeters,
		double RollDegrees,
		double PitchDegrees,
		SunPosition Sun,
		double LocalEastMeters,
		double LocalNorthMeters,
		double SmoothedGForce,
		double LateralAccelG,
		double LongitudinalAccelG)
	{
		public static CachedDerivedFrame From(DerivedFrame frame)
		{
			return new CachedDerivedFrame(frame.SpeedKmh, frame.HeadingDegrees, frame.GradientPercent,
				frame.CumulativeDistanceMeters, frame.RollDegrees, frame.PitchDegrees, frame.Sun, frame.LocalEastMeters,
				frame.LocalNorthMeters, frame.SmoothedGForce, frame.LateralAccelG, frame.LongitudinalAccelG);
		}

		public DerivedFrame ToDerivedFrame(TelemetryFrame raw)
		{
			return new DerivedFrame(raw, SpeedKmh, HeadingDegrees, GradientPercent, CumulativeDistanceMeters,
				RollDegrees, PitchDegrees, Sun, LocalEastMeters, LocalNorthMeters, SmoothedGForce, LateralAccelG, LongitudinalAccelG,
				raw.StartsAfterGap);
		}
	}
}
