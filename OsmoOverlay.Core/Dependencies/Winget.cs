using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace OsmoOverlay.Core.Dependencies;

internal static partial class Winget
{
	public const int PackageAlreadyInstalled = unchecked((int)0x8A150061);
	public const int NoApplicableUpgrade = unchecked((int)0x8A15002B);

	// Pinned to the community source: without it winget may also match msstore and stop to ask which one.
	private const string Source = "winget";
	private const string PortablePackageDirSuffix = "_Microsoft.Winget.Source_8wekyb3d8bbwe";

	// `show --versions` lists one version per line under a localized header ("Version" / "Wersja", then
	// dashes), so match lines that are nothing but a version instead of the label.
	[GeneratedRegex(@"^\s*(\d+(?:\.\d+)+)\s*$", RegexOptions.Multiline)]
	private static partial Regex VersionLineRegex();

	/// <summary>A null version means the latest winget has.</summary>
	public static string[] InstallArgs(string packageId, string? version)
	{
		return ["install", .. ManageArgs(packageId, version)];
	}

	/// <inheritdoc cref="InstallArgs" />
	public static string[] UpgradeArgs(string packageId, string? version)
	{
		return ["upgrade", .. ManageArgs(packageId, version)];
	}

	private static string[] ManageArgs(string packageId, string? version)
	{
		return
		[
			"--id", packageId, "--exact", .. version is null ? Array.Empty<string>() : ["--version", version],
			"--source", Source, "--silent", "--disable-interactivity", "--accept-package-agreements", "--accept-source-agreements"
		];
	}

	public static ProcessStartInfo CreateStartInfo(bool quiet, params string[] args)
	{
		ProcessStartInfo psi = quiet ? ProcessHelper.CreateHiddenQuiet("winget", args) : ProcessHelper.CreateHidden("winget", args);
		psi.StandardOutputEncoding = Encoding.UTF8;
		psi.StandardErrorEncoding = Encoding.UTF8;
		return psi;
	}

	/// <summary>
	///     The winget package the tool's executable on PATH actually comes from, or null when it wasn't
	///     installed through winget (manual download, Scoop, Chocolatey) - upgrading any other id wouldn't
	///     touch the binary this app runs. Portable packages (FFmpeg) are read straight from their folder
	///     (...\WinGet\Packages\&lt;id&gt;_&lt;source&gt;\...); installer-based ones (ExifTool) fall back
	///     to asking winget whether the tool's own id is installed.
	/// </summary>
	public static async Task<string?> FindInstalledPackageIdAsync(ExternalTool tool, CancellationToken ct)
	{
		if (DependencyChecker.FindExecutable(tool.Commands[0]) is { } executable &&
		    PackageIdFromPortablePath(executable) is { } portableId)
			return portableId;

		var (exitCode, stdout, _) = await ProcessHelper.TryRunCapturedAsync(
			CreateStartInfo(true, "list", "--id", tool.WingetId, "--exact", "--source", Source,
				"--disable-interactivity", "--accept-source-agreements"), ct);

		return exitCode == 0 && stdout.Contains(tool.WingetId, StringComparison.OrdinalIgnoreCase) ? tool.WingetId : null;
	}

	/// <summary>Every version winget offers for the package; empty when they can't be read.</summary>
	public static async Task<IReadOnlyList<string>> GetAvailableVersionsAsync(string packageId, CancellationToken ct)
	{
		var (exitCode, stdout, _) = await ProcessHelper.TryRunCapturedAsync(
			CreateStartInfo(true, "show", "--id", packageId, "--exact", "--source", Source, "--versions",
				"--disable-interactivity", "--accept-source-agreements"), ct);
		return exitCode == 0 ? ParseVersionList(stdout) : [];
	}

	internal static IReadOnlyList<string> ParseVersionList(string showVersionsOutput)
	{
		return VersionLineRegex().Matches(showVersionsOutput).Select(m => m.Groups[1].Value).ToList();
	}

	private static string? PackageIdFromPortablePath(string executable)
	{
		string resolved;
		try
		{
			resolved = new FileInfo(executable).ResolveLinkTarget(true)?.FullName ?? executable;
		}
		catch (IOException)
		{
			resolved = executable;
		}

		var segments = resolved.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		for (var i = 1; i < segments.Length - 1; i++)
		{
			if (!segments[i - 1].Equals("WinGet", StringComparison.OrdinalIgnoreCase) ||
			    !segments[i].Equals("Packages", StringComparison.OrdinalIgnoreCase))
				continue;

			var packageDir = segments[i + 1];
			return packageDir.EndsWith(PortablePackageDirSuffix, StringComparison.OrdinalIgnoreCase)
				? packageDir[..^PortablePackageDirSuffix.Length]
				: null;
		}

		return null;
	}
}
