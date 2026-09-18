using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

internal static class FileSummaryCache
{
	// Bump whenever telemetry extraction or derivation logic changes, so stale cache
	// entries computed with the old logic are treated as a cache miss automatically.
	public const int FormatVersion = 12;

	private static readonly string CacheDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "cache");

	/// <summary>
	///     Summary is null on any kind of miss (no file, corrupt, stamps don't match, stale format).
	///     StaleFormatVersion is set only for that last case - a cache entry that otherwise matches this
	///     exact file (same path/size/mtime) but was written by an older FormatVersion - so callers can
	///     tell "never analyzed before" apart from "analyzed before, but the logic has changed since" and
	///     report that distinctly instead of a generic cache miss.
	/// </summary>
	public static (FileSummary? Summary, int? StaleFormatVersion) TryLoad(IReadOnlyList<string> inputPaths)
	{
		var cachePath = GetCachePath(inputPaths);
		if (!File.Exists(cachePath)) return (null, null);

		try
		{
			var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(cachePath));
			if (entry is null) return (null, null);
			if (entry.Files.Count != inputPaths.Count) return (null, null);

			for (var i = 0; i < inputPaths.Count; i++)
			{
				var info = new FileInfo(inputPaths[i]);
				if (entry.Files[i].FileSizeBytes != info.Length ||
				    entry.Files[i].LastWriteTimeUtcTicks != info.LastWriteTimeUtc.Ticks)
					return (null, null);
			}

			if (entry.FormatVersion != FormatVersion) return (null, entry.FormatVersion);

			return (Reinflate(entry), null);
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, $"Cache entry for {string.Join(", ", inputPaths)} is corrupt or unreadable, treating as a cache miss");
			return (null, null);
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

			// DerivedFrame.Raw is a full copy of the matching TelemetryFrames entry (TelemetryProcessor
			// builds each DerivedFrame from exactly one TelemetryFrame, 1:1 by index) - serializing it
			// verbatim would store every raw sample twice, roughly doubling the file for a telemetry-
			// heavy recording. Strip it here and pair DerivedExtras back up with TelemetryFrames by
			// index in Reinflate instead.
			List<CachedDerivedFrame>? derivedExtras = summary.DerivedFrames?.Select(CachedDerivedFrame.From).ToList();
			var entry = new CacheEntry(FormatVersion, files, summary with { DerivedFrames = null }, derivedExtras);
			AtomicFile.WriteAllText(GetCachePath(inputPaths), JsonSerializer.Serialize(entry));
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

	/// <summary>
	///     Rebuilds Summary.DerivedFrames from DerivedExtras + Summary.TelemetryFrames (see Save). Falls
	///     back to whatever Summary.DerivedFrames already deserialized to - i.e. the pre-dedup on-disk
	///     shape, still readable since DerivedExtras is simply absent from that older JSON - when there's
	///     nothing to reinflate, and treats a count mismatch as a corrupt entry (caught by TryLoad's
	///     caller, same as any other unreadable cache file) rather than reinflating out of bounds.
	/// </summary>
	private static FileSummary Reinflate(CacheEntry entry)
	{
		if (entry.DerivedExtras is null) return entry.Summary;

		IReadOnlyList<TelemetryFrame> raw = entry.Summary.TelemetryFrames
		                                    ?? throw new InvalidDataException("Cache entry has DerivedExtras but no TelemetryFrames to pair them with.");
		if (raw.Count != entry.DerivedExtras.Count)
			throw new InvalidDataException("Cache entry's DerivedExtras count doesn't match TelemetryFrames.");

		var derivedFrames = new List<DerivedFrame>(raw.Count);
		for (var i = 0; i < raw.Count; i++)
			derivedFrames.Add(entry.DerivedExtras[i].ToDerivedFrame(raw[i]));

		return entry.Summary with { DerivedFrames = derivedFrames };
	}

	private sealed record FileStamp(long FileSizeBytes, long LastWriteTimeUtcTicks);

	private sealed record CacheEntry(int FormatVersion, List<FileStamp> Files, FileSummary Summary, List<CachedDerivedFrame>? DerivedExtras = null);

	/// <summary>DerivedFrame minus Raw (see Save) - everything TelemetryProcessor computes from one TelemetryFrame, paired back up with it by index in Reinflate.</summary>
	private sealed record CachedDerivedFrame(
		double SpeedKmh, double HeadingDegrees, double GradientPercent, double CumulativeDistanceMeters,
		double PitchDegrees, SunPosition Sun, double LocalEastMeters, double LocalNorthMeters, double SmoothedGForce)
	{
		public static CachedDerivedFrame From(DerivedFrame frame)
		{
			return new CachedDerivedFrame(frame.SpeedKmh, frame.HeadingDegrees, frame.GradientPercent,
				frame.CumulativeDistanceMeters, frame.PitchDegrees, frame.Sun, frame.LocalEastMeters,
				frame.LocalNorthMeters, frame.SmoothedGForce);
		}

		public DerivedFrame ToDerivedFrame(TelemetryFrame raw)
		{
			return new DerivedFrame(raw, SpeedKmh, HeadingDegrees, GradientPercent, CumulativeDistanceMeters,
				PitchDegrees, Sun, LocalEastMeters, LocalNorthMeters, SmoothedGForce);
		}
	}
}
