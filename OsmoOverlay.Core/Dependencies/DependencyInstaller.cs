using OsmoOverlay.Core.Localization;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Core.Dependencies;

public sealed record InstallResult(bool Success, string Message);

public static class DependencyInstaller
{
	private static readonly LinuxPackageManager[] LinuxPackageManagers =
	[
		// update first: on a fresh or long-idle system the local index points at package versions the
		// mirrors no longer have, and install fails with 404s.
		new("apt-get", t => t.AptPackage, ["install", "-y"], ["install", "--only-upgrade", "-y"], ["update"],
			"DEBIAN_FRONTEND=noninteractive "),
		new("dnf", t => t.DnfPackage, ["install", "-y"], ["upgrade", "-y"], null, ""),
		// No -y on purpose: -Sy without -u is a partial upgrade, which Arch explicitly doesn't support.
		new("pacman", t => t.PacmanPackage, ["-S", "--noconfirm", "--needed"], ["-S", "--noconfirm"], null, "")
	];

	public static bool CanAttemptAutoInstall()
	{
		if (OperatingSystem.IsWindows()) return DependencyChecker.IsCommandAvailable("winget");
		if (OperatingSystem.IsMacOS()) return DependencyChecker.IsCommandAvailable("brew");
		if (OperatingSystem.IsLinux()) return FindLinuxPackageManager() is not null;
		return false;
	}

	/// <summary>
	///     Success means the tool is actually usable afterwards, not just that the package manager exited
	///     cleanly - a package can be installed yet unreachable (e.g. PATH not refreshed yet).
	/// </summary>
	public static async Task<InstallResult> InstallAsync(ExternalTool tool, Action<string> onOutput, CancellationToken ct)
	{
		DependencyChecker.RefreshProcessPath();
		if (DependencyChecker.IsAvailable(tool)) return new InstallResult(true, string.Format(CoreStrings.Install_AlreadyInstalled, tool.DisplayName));

		InstallResult result = await RunPackageManagerAsync(tool, false, onOutput, ct);
		if (!result.Success) return result;

		DependencyChecker.RefreshProcessPath();
		if (DependencyChecker.IsAvailable(tool)) return new InstallResult(true, string.Format(CoreStrings.Install_Installed, tool.DisplayName));
		if (!tool.Commands.All(DependencyChecker.IsCommandAvailable))
		{
			return new InstallResult(false,
				string.Format(CoreStrings.Install_NotOnPath, tool.DisplayName, string.Join("/", tool.Commands)));
		}

		// The package manager's build is too old (or a static one) for the preview's libraries.
		return new InstallResult(false, string.Format(CoreStrings.Install_PreviewCantUse, tool.DisplayName, LibavLoader.TryLoad()));
	}

	/// <summary>
	///     Upgrades in place - for FFmpeg with its libraries loaded, only where UpgradeNeedsRestart is false. The
	///     libraries in this process then stay the old ones until it restarts, which the result says.
	/// </summary>
	public static async Task<InstallResult> UpgradeAsync(ExternalTool tool, Action<string> onOutput, CancellationToken ct)
	{
		InstallResult result = await RunPackageManagerAsync(tool, true, onOutput, ct);
		if (!result.Success) return result;

		// A portable winget upgrade moves the tool to a new versioned folder and rewrites PATH.
		DependencyChecker.RefreshProcessPath();
		return tool.NeedsSharedLibraries && LibavLoader.IsLoaded
			? result with { Message = string.Format(CoreStrings.Install_RestartToUse, result.Message) }
			: result;
	}

	/// <summary>
	///     Windows keeps a loaded DLL locked, and winget's upgrade of a portable package deletes the old version's
	///     folder - so FFmpeg can't be upgraded while this process has its libraries loaded (from the dependency
	///     check at startup on). StartUpgradeAfterExitAsync does it once the app has closed instead. Elsewhere a
	///     loaded library can be replaced on disk, so UpgradeAsync works and only asks for a restart.
	/// </summary>
	public static bool UpgradeNeedsRestart(ExternalTool tool)
	{
		return OperatingSystem.IsWindows() && tool.NeedsSharedLibraries && LibavLoader.IsLoaded;
	}

	/// <summary>
	///     Starts a visible PowerShell window that waits for this process to exit, runs the winget upgrade and starts
	///     the app again - the caller shuts the app down right after a successful result. Windows only.
	/// </summary>
	public static async Task<InstallResult> StartUpgradeAfterExitAsync(ExternalTool tool, CancellationToken ct)
	{
		if (!OperatingSystem.IsWindows()) return new InstallResult(false, "Upgrading after exit is only needed on Windows");
		if (await Winget.FindInstalledPackageIdAsync(tool, ct) is not { } packageId) return NotFromWinget(tool);

		(bool found, string? version) = await ResolveWingetVersionAsync(tool, packageId, ct);
		if (!found) return NoSupportedVersion(tool, packageId);

		try
		{
			// Deliberately not ProcessHelper: this window has to stay visible and outlive the app.
			var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = false };
			psi.ArgumentList.Add("-NoProfile");
			psi.ArgumentList.Add("-EncodedCommand");
			psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildUpgradeAfterExitScript(tool, packageId, version))));
			Process.Start(psi)?.Dispose();
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
		{
			return new InstallResult(false, string.Format(CoreStrings.Install_CouldNotStartUpdate, ex.Message));
		}

		return new InstallResult(true, version is null
			? string.Format(CoreStrings.Install_WillUpdateLatest, tool.DisplayName)
			: string.Format(CoreStrings.Install_WillUpdateTo, tool.DisplayName, version));
	}

	private static string BuildUpgradeAfterExitScript(ExternalTool tool, string packageId, string? version)
	{
		(string appPath, string[] appArgs) = AppCommand.Current();
		string wingetArgs = string.Join(' ', Winget.UpgradeArgs(packageId, version).Select(PowerShellLiteral));
		string relaunch = $"Start-Process -FilePath {PowerShellLiteral(appPath)}" +
		                  (appArgs.Length == 0 ? "" : $" -ArgumentList {string.Join(',', appArgs.Select(PowerShellLiteral))}");

		return string.Join('\n',
			$"$Host.UI.RawUI.WindowTitle = {PowerShellLiteral($"Updating {tool.DisplayName}")}",
			"Write-Host 'Waiting for OsmoOverlay to close...'",
			$"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue",
			$"& winget {wingetArgs}",
			"$code = $LASTEXITCODE",
			$"if ($code -ne 0 -and $code -ne {Winget.NoApplicableUpgrade}) {{",
			"    Write-Host \"winget exited with code $code\"",
			"    Read-Host 'Press Enter to start OsmoOverlay again'",
			"}",
			relaunch);
	}

	private static string PowerShellLiteral(string value)
	{
		return $"'{value.Replace("'", "''")}'";
	}

	/// <summary>
	///     The version winget installs: the newest within the tool's SupportedMajorVersion, since winget's latest may
	///     be a major this app can't load. Found is false when winget has none of that major; without a supported
	///     major, Version stays null (winget's latest).
	/// </summary>
	private static async Task<(bool Found, string? Version)> ResolveWingetVersionAsync(ExternalTool tool, string packageId,
		CancellationToken ct)
	{
		if (tool.SupportedMajorVersion is not { } major) return (true, null);

		string? version = DependencyVersionChecker.PickLatest(await Winget.GetAvailableVersionsAsync(packageId, ct), major);
		return (version is not null, version);
	}

	private static InstallResult NotFromWinget(ExternalTool tool)
	{
		return new InstallResult(false,
			string.Format(CoreStrings.Install_NotFromWinget, tool.DisplayName));
	}

	private static InstallResult NoSupportedVersion(ExternalTool tool, string packageId)
	{
		return new InstallResult(false,
			string.Format(CoreStrings.Install_NoSupportedVersion, tool.DisplayName, tool.SupportedMajorVersion, packageId));
	}

	private static async Task<InstallResult> RunPackageManagerAsync(ExternalTool tool, bool upgrade, Action<string> onOutput,
		CancellationToken ct)
	{
		if (OperatingSystem.IsWindows())
			return upgrade ? await UpgradeWithWingetAsync(tool, onOutput, ct) : await InstallWithWingetAsync(tool, onOutput, ct);

		if (OperatingSystem.IsMacOS())
		{
			if (!DependencyChecker.IsCommandAvailable("brew"))
				return new InstallResult(false, CoreStrings.Install_NoHomebrew);

			int exitCode = await RunAsync(ProcessHelper.CreateHidden("brew", upgrade ? "upgrade" : "install", tool.BrewPackage),
				onOutput, ct);
			return exitCode == 0
				? new InstallResult(true, string.Format(CoreStrings.Install_Finished, $"brew {(upgrade ? "upgrade" : "install")} {tool.BrewPackage}"))
				: new InstallResult(false, string.Format(CoreStrings.Install_ExitCode, "brew", exitCode));
		}

		if (OperatingSystem.IsLinux()) return await RunLinuxPackageManagerAsync(tool, upgrade, onOutput, ct);

		return new InstallResult(false, CoreStrings.Install_UnsupportedPlatform);
	}

	private static async Task<InstallResult> InstallWithWingetAsync(ExternalTool tool, Action<string> onOutput, CancellationToken ct)
	{
		(bool found, string? version) = await ResolveWingetVersionAsync(tool, tool.WingetId, ct);
		if (!found) return NoSupportedVersion(tool, tool.WingetId);

		int exitCode = await RunAsync(Winget.CreateStartInfo(false, Winget.InstallArgs(tool.WingetId, version)), onOutput, ct);

		// "Already installed" / "no upgrade available" still count here - InstallAsync then checks whether
		// the tool is actually reachable, which is what matters.
		return exitCode is 0 or Winget.PackageAlreadyInstalled or Winget.NoApplicableUpgrade
			? new InstallResult(true, string.Format(CoreStrings.Install_Finished, $"winget install {tool.WingetId}"))
			: new InstallResult(false, string.Format(CoreStrings.Install_ExitCode, "winget", $"0x{exitCode:X8}"));
	}

	private static async Task<InstallResult> UpgradeWithWingetAsync(ExternalTool tool, Action<string> onOutput, CancellationToken ct)
	{
		if (await Winget.FindInstalledPackageIdAsync(tool, ct) is not { } packageId) return NotFromWinget(tool);

		(bool found, string? version) = await ResolveWingetVersionAsync(tool, packageId, ct);
		if (!found) return NoSupportedVersion(tool, packageId);

		onOutput(version is null
			? string.Format(CoreStrings.Install_UpgradingPackage, packageId)
			: string.Format(CoreStrings.Install_UpgradingPackageTo, packageId, version));
		int exitCode = await RunAsync(Winget.CreateStartInfo(false, Winget.UpgradeArgs(packageId, version)), onOutput, ct);

		return exitCode switch
		{
			0 => new InstallResult(true, string.Format(CoreStrings.Install_Updated, tool.DisplayName)),
			Winget.NoApplicableUpgrade => new InstallResult(true, string.Format(CoreStrings.Install_AlreadyUpToDate, tool.DisplayName)),
			_ => new InstallResult(false, string.Format(CoreStrings.Install_ExitCode, "winget", $"0x{exitCode:X8}"))
		};
	}

	/// <summary>
	///     One elevation prompt for the whole sequence (index refresh + install): pkexec runs a single
	///     `sh -c` instead of one pkexec per step. Package names are fixed constants from RequiredTools,
	///     never user input, so building the script by concatenation is safe.
	/// </summary>
	private static async Task<InstallResult> RunLinuxPackageManagerAsync(ExternalTool tool, bool upgrade, Action<string> onOutput,
		CancellationToken ct)
	{
		if (FindLinuxPackageManager() is not { } manager)
		{
			return new InstallResult(false,
				string.Format(CoreStrings.Install_NoPackageManager, tool.DisplayName));
		}

		List<string> steps = [];
		if (manager.RefreshArgs is not null) steps.Add(string.Join(' ', [manager.Command, .. manager.RefreshArgs]));
		steps.Add(string.Join(' ', [manager.Command, .. upgrade ? manager.UpgradeArgs : manager.InstallArgs, manager.Package(tool)]));

		if (!DependencyChecker.IsCommandAvailable("pkexec"))
		{
			return new InstallResult(false,
				string.Format(CoreStrings.Install_RunInTerminal, string.Join(" && ", steps.Select(s => "sudo " + s))));
		}

		string script = string.Join(" && ", steps.Select(s => manager.EnvPrefix + s));
		int exitCode = await RunAsync(ProcessHelper.CreateHidden("pkexec", "sh", "-c", script), onOutput, ct);

		// 126/127: pkexec's own "not authorized" / "dismissed", as opposed to the package manager failing.
		return exitCode switch
		{
			0 => new InstallResult(true, string.Format(CoreStrings.Install_Finished, manager.Command)),
			126 or 127 => new InstallResult(false, CoreStrings.Install_AuthorizationDenied),
			_ => new InstallResult(false, string.Format(CoreStrings.Install_ExitCode, manager.Command, exitCode))
		};
	}

	private static LinuxPackageManager? FindLinuxPackageManager()
	{
		return LinuxPackageManagers.FirstOrDefault(m => DependencyChecker.IsCommandAvailable(m.Command));
	}

	/// <summary>Streams output line by line; cancelling kills the whole process tree instead of leaving it running unobserved.</summary>
	private static async Task<int> RunAsync(ProcessStartInfo psi, Action<string> onOutput, CancellationToken ct)
	{
		using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {psi.FileName}.");

		DataReceivedEventHandler forward = (_, e) =>
		{
			if (e.Data is not null && !IsProgressNoise(e.Data)) onOutput(e.Data.TrimEnd());
		};
		process.OutputDataReceived += forward;
		process.ErrorDataReceived += forward;
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		try
		{
			await process.WaitForExitAsync(ct);
		}
		catch (OperationCanceledException)
		{
			try
			{
				process.Kill(true);
			}
			catch (InvalidOperationException)
			{
				// Exited in the meantime.
			}

			throw;
		}

		return process.ExitCode;
	}

	/// <summary>Spinner frames ("-", "\", "|", "/") and download progress bars - winget redraws them with \r even when redirected.</summary>
	private static bool IsProgressNoise(string line)
	{
		string trimmed = line.Trim();
		return trimmed.Length == 0 || trimmed is "-" or "\\" or "|" or "/" || trimmed.Contains('█') || trimmed.Contains('▒');
	}

	private sealed record LinuxPackageManager(
		string Command,
		Func<ExternalTool, string> Package,
		string[] InstallArgs,
		string[] UpgradeArgs,
		string[]? RefreshArgs,
		string EnvPrefix);
}
