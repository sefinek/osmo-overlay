using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Dependencies;

public sealed record ToolVersionInfo(ExternalTool Tool, string? InstalledVersion, string? LatestVersion, bool UpdateAvailable);

public static partial class DependencyVersionChecker
{
	/// <summary>Matches the first dotted numeric version in free-form tool/package-manager output, e.g. pulls "9.0.1" out of "ffmpeg version 9.0.1-full_build-www.gyan.dev" or "6.1.1" out of apt's epoch-prefixed "7:6.1.1-3ubuntu5".</summary>
	[GeneratedRegex(@"\d+(?:\.\d+)+")]
	private static partial Regex VersionNumberRegex();

	public static async Task<IReadOnlyList<ToolVersionInfo>> CheckAllAsync(IEnumerable<ExternalTool> tools, CancellationToken ct,
		Action<string>? onProgress = null)
	{
		return await Task.WhenAll(tools.Select(t => CheckAsync(t, ct, onProgress)));
	}

	/// <summary>
	///     Notifies (not just file-logs) each step - unlike the routine, high-frequency process spawns
	///     this drives internally (CreateHiddenQuiet), a user pressing "Check for updates" is watching and
	///     waiting, so silence here would look like the app hung, especially on Linux/macOS where a
	///     package-manager query can take a couple of seconds. onProgress is a second, optional route for
	///     the same lines - AppLogger.Notify reaches the main window's LOG panel, but a caller showing its
	///     own live status label (Settings' About tab) wants these lines directly, without picking up
	///     unrelated Notify traffic from elsewhere in the app.
	/// </summary>
	public static async Task<ToolVersionInfo> CheckAsync(ExternalTool tool, CancellationToken ct, Action<string>? onProgress = null)
	{
		Report($"Checking {tool.DisplayName} version...", onProgress);

		Task<string?> installedTask = GetInstalledVersionAsync(tool, ct);
		Task<(string? Version, bool CanUpgrade)> latestTask = GetLatestVersionAsync(tool, ct);
		await Task.WhenAll(installedTask, latestTask);

		var installed = installedTask.Result;
		var (latest, canUpgrade) = latestTask.Result;
		var newerExists = IsOlder(installed, latest);
		var updateAvailable = newerExists && canUpgrade;

		Report(DescribeResult(tool.DisplayName, installed, latest, newerExists, canUpgrade), onProgress);
		return new ToolVersionInfo(tool, installed, latest, updateAvailable);
	}

	private static void Report(string message, Action<string>? onProgress)
	{
		AppLogger.Notify(message);
		onProgress?.Invoke(message);
	}

	private static string DescribeResult(string displayName, string? installed, string? latest, bool newerExists, bool canUpgrade)
	{
		if (installed is null) return $"{displayName}: not installed, or its version could not be read.";
		if (latest is null) return $"{displayName}: installed {installed} (couldn't determine the latest version).";
		if (!newerExists) return $"{displayName}: installed {installed} - up to date.";
		return canUpgrade
			? $"{displayName}: installed {installed}, update available ({latest})."
			: $"{displayName}: installed {installed}, {latest} is available - not installed through the package manager, update it manually.";
	}

	private static async Task<string?> GetInstalledVersionAsync(ExternalTool tool, CancellationToken ct)
	{
		var (exitCode, stdout, stderr) = await RunAsync(tool.VersionCommand, [.. tool.VersionArgs], ct);
		if (exitCode != 0) return null;

		// exiftool's -ver prints a bare number on stdout; ffmpeg's -version prints a sentence on stdout,
		// but check stderr too in case a build ever routes it there instead.
		return ExtractVersionNumber(stdout) ?? ExtractVersionNumber(stderr);
	}

	/// <summary>
	///     CanUpgrade is false only when it's known that DependencyInstaller.UpgradeAsync couldn't reach the
	///     installed copy (Windows: the tool on PATH doesn't come from winget) - the newer version is still
	///     reported, just without offering an Update button that would fail.
	/// </summary>
	private static async Task<(string? Version, bool CanUpgrade)> GetLatestVersionAsync(ExternalTool tool, CancellationToken ct)
	{
		if (OperatingSystem.IsWindows()) return await GetLatestFromWingetAsync(tool, ct);
		if (OperatingSystem.IsMacOS()) return (await GetLatestFromBrewAsync(tool, ct), true);
		if (OperatingSystem.IsLinux()) return (await GetLatestFromLinuxPackageManagerAsync(tool, ct), true);
		return (null, false);
	}

	private static async Task<(string? Version, bool CanUpgrade)> GetLatestFromWingetAsync(ExternalTool tool, CancellationToken ct)
	{
		if (!DependencyChecker.IsCommandAvailable("winget")) return (null, false);

		var installedId = await Winget.FindInstalledPackageIdAsync(tool, ct);
		return (await Winget.GetLatestVersionAsync(installedId ?? tool.WingetId, ct), installedId is not null);
	}

	private static async Task<string?> GetLatestFromBrewAsync(ExternalTool tool, CancellationToken ct)
	{
		if (!DependencyChecker.IsCommandAvailable("brew")) return null;

		var (exitCode, stdout, _) = await RunAsync("brew", ["info", "--json=v2", tool.BrewPackage], ct);
		if (exitCode != 0) return null;

		var stable = JsonNode.Parse(stdout)?["formulae"]?.AsArray().FirstOrDefault()?["versions"]?["stable"]?.GetValue<string>();
		return stable is null ? null : ExtractVersionNumber(stable);
	}

	/// <summary>
	///     Reads whatever the package manager's local index/cache already has (apt-cache policy, dnf
	///     --cacheonly, pacman's synced db) instead of forcing a metadata refresh - that would mean
	///     shelling out to a privileged, network-touching command just to *check* a version, which is a
	///     much bigger ask than a read-only query. The tradeoff: on a machine whose package index hasn't
	///     synced in a while, "latest" here means "latest known to the local cache", not necessarily
	///     the true upstream latest.
	/// </summary>
	private static async Task<string?> GetLatestFromLinuxPackageManagerAsync(ExternalTool tool, CancellationToken ct)
	{
		if (DependencyChecker.IsCommandAvailable("apt-cache"))
		{
			var (exitCode, stdout, _) = await RunAsync("apt-cache", ["policy", tool.AptPackage], ct);
			if (exitCode != 0) return null;

			var candidateLine = stdout.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("Candidate:"));
			return candidateLine is null ? null : ExtractVersionNumber(candidateLine);
		}

		if (DependencyChecker.IsCommandAvailable("dnf"))
		{
			var (exitCode, stdout, _) = await RunAsync("dnf", ["--cacheonly", "list", "available", tool.DnfPackage], ct);
			if (exitCode != 0) return null;

			return ExtractVersionNumber(stdout);
		}

		if (DependencyChecker.IsCommandAvailable("pacman"))
		{
			var (exitCode, stdout, _) = await RunAsync("pacman", ["-Si", tool.PacmanPackage], ct);
			if (exitCode != 0) return null;

			var versionLine = stdout.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("Version"));
			return versionLine is null ? null : ExtractVersionNumber(versionLine);
		}

		return null;
	}

	private static string? ExtractVersionNumber(string text)
	{
		Match match = VersionNumberRegex().Match(text);
		return match.Success ? match.Value : null;
	}

	private static bool IsOlder(string? installed, string? latest)
	{
		return installed is not null && latest is not null &&
		       Version.TryParse(installed, out Version? installedVersion) &&
		       Version.TryParse(latest, out Version? latestVersion) &&
		       installedVersion < latestVersion;
	}

	private static Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string command, string[] args, CancellationToken ct)
	{
		return ProcessHelper.TryRunCapturedAsync(ProcessHelper.CreateHiddenQuiet(command, args), ct);
	}
}
