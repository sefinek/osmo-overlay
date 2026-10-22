using System.Diagnostics;
using System.Text.RegularExpressions;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Core.Updates;

namespace OsmoOverlay.Core;

/// <summary>What ExportMarker.Comment says, read back from a file.</summary>
public sealed record ExportMarkerInfo(string Version, int TelemetryVersion, OrientationSource Orientation);

/// <summary>
///     What a render says about itself in the file's own metadata (the container's "comment"): the program and its
///     version, the version of the telemetry logic (FileSummaryCache.FormatVersion) and where the tilt came from - enough
///     to tell which release made a file when two renders of one recording are compared. Plain ASCII, English whatever the
///     interface's language: it is read by other programs and other people.
/// </summary>
public static partial class ExportMarker
{
	public static string Comment(OrientationSource orientation)
	{
		string source = orientation == OrientationSource.Quaternion ? "camera orientation" : "accelerometer";
		return $"OsmoOverlay {AppUpdates.CurrentVersion} (telemetry {FileSummaryCache.FormatVersion}, tilt from {source}) - {AppUpdates.RepositoryUrl} - {AppUpdates.WebsiteUrl}";
	}

	/// <summary>The marker in a comment, null for one this program didn't write (or a file without a comment).</summary>
	public static ExportMarkerInfo? TryParse(string? comment)
	{
		if (comment is null || CommentPattern().Match(comment) is not { Success: true } match) return null;
		if (!int.TryParse(match.Groups["telemetry"].Value, out int telemetry)) return null;

		return new ExportMarkerInfo(match.Groups["version"].Value, telemetry,
			match.Groups["source"].Value == "camera orientation" ? OrientationSource.Quaternion : OrientationSource.Accelerometer);
	}

	/// <summary>The marker in a file's metadata through ffprobe - null for a file without one, or one ffprobe can't read.</summary>
	public static ExportMarkerInfo? Read(string path)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffprobe",
			"-v", "error", "-show_entries", "format_tags=comment", "-of", "default=noprint_wrappers=1:nokey=1", path);
		(int exitCode, string stdout, _) = ProcessHelper.RunCaptured(psi);
		return exitCode == 0 ? TryParse(stdout.Trim()) : null;
	}

	[GeneratedRegex(@"^OsmoOverlay (?<version>\S+) \(telemetry (?<telemetry>\d+), tilt from (?<source>accelerometer|camera orientation)\)")]
	private static partial Regex CommentPattern();
}
