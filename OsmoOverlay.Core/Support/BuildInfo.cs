using System.Globalization;
using System.Reflection;

namespace OsmoOverlay.Core;

/// <summary>
///     When (and from which commit) this build was made. The release workflow sets BUILD_DATE, which Core's project writes
///     into the assembly (BuildDate, plus Commit from GITHUB_SHA); a local build has no stamp, so the Core library's own
///     file time stands in - there it is the time of the compile.
/// </summary>
public static class BuildInfo
{
	private static readonly Dictionary<string, string?> Metadata = typeof(BuildInfo).Assembly
		.GetCustomAttributes<AssemblyMetadataAttribute>()
		.ToDictionary(a => a.Key, a => a.Value);

	/// <summary>Null when neither a stamp nor the file is there (a single-file publish without one).</summary>
	public static DateTime? BuiltAtUtc
	{
		get
		{
			if (Metadata.TryGetValue("BuildDate", out string? stamp) &&
			    DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime date))
				return date;

			string path = typeof(BuildInfo).Assembly.Location;
			return path.Length > 0 && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
		}
	}

	public static string? Commit => Metadata.GetValueOrDefault("Commit");

	/// <summary>The date in the local time zone, with the commit when the build has one: "2026-10-23 14:05 (a1b2c3d)".</summary>
	public static string Describe()
	{
		string date = BuiltAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "?";
		return Commit is { Length: > 0 } commit ? $"{date} ({commit})" : date;
	}
}
