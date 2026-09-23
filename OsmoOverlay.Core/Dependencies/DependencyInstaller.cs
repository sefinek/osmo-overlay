using System.Diagnostics;

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
		if (DependencyChecker.IsAvailable(tool)) return new InstallResult(true, $"{tool.DisplayName} is already installed");

		InstallResult result = await RunPackageManagerAsync(tool, false, onOutput, ct);
		if (!result.Success) return result;

		DependencyChecker.RefreshProcessPath();
		return DependencyChecker.IsAvailable(tool)
			? new InstallResult(true, $"{tool.DisplayName} installed")
			: new InstallResult(false,
				$"The package manager reports {tool.DisplayName} as installed, but {string.Join("/", tool.Commands)} " +
				"still can't be found on PATH. Restart the app, or add its folder to PATH manually");
	}

	public static async Task<InstallResult> UpgradeAsync(ExternalTool tool, Action<string> onOutput, CancellationToken ct)
	{
		InstallResult result = await RunPackageManagerAsync(tool, true, onOutput, ct);

		// A portable winget upgrade moves the tool to a new versioned folder and rewrites PATH.
		if (result.Success) DependencyChecker.RefreshProcessPath();
		return result;
	}

	private static async Task<InstallResult> RunPackageManagerAsync(ExternalTool tool, bool upgrade, Action<string> onOutput,
		CancellationToken ct)
	{
		if (OperatingSystem.IsWindows())
			return upgrade ? await UpgradeWithWingetAsync(tool, onOutput, ct) : await InstallWithWingetAsync(tool, onOutput, ct);

		if (OperatingSystem.IsMacOS())
		{
			if (!DependencyChecker.IsCommandAvailable("brew"))
				return new InstallResult(false, "Homebrew is not installed. Install it from https://brew.sh, then retry");

			var exitCode = await RunAsync(ProcessHelper.CreateHidden("brew", upgrade ? "upgrade" : "install", tool.BrewPackage),
				onOutput, ct);
			return exitCode == 0
				? new InstallResult(true, $"brew {(upgrade ? "upgrade" : "install")} {tool.BrewPackage} finished")
				: new InstallResult(false, $"brew exited with code {exitCode}");
		}

		if (OperatingSystem.IsLinux()) return await RunLinuxPackageManagerAsync(tool, upgrade, onOutput, ct);

		return new InstallResult(false, "Automatic installation is not supported on this platform");
	}

	private static async Task<InstallResult> InstallWithWingetAsync(ExternalTool tool, Action<string> onOutput, CancellationToken ct)
	{
		var exitCode = await RunAsync(Winget.CreateStartInfo(false, Winget.InstallArgs(tool.WingetId)), onOutput, ct);

		// "Already installed" / "no upgrade available" still count here - InstallAsync then checks whether
		// the tool is actually reachable, which is what matters.
		return exitCode is 0 or Winget.PackageAlreadyInstalled or Winget.NoApplicableUpgrade
			? new InstallResult(true, $"winget install {tool.WingetId} finished")
			: new InstallResult(false, $"winget exited with code 0x{exitCode:X8}");
	}

	private static async Task<InstallResult> UpgradeWithWingetAsync(ExternalTool tool, Action<string> onOutput, CancellationToken ct)
	{
		if (await Winget.FindInstalledPackageIdAsync(tool, ct) is not { } packageId)
			return new InstallResult(false,
				$"{tool.DisplayName} wasn't installed through winget, so it can't be updated from here - " +
				"update it the same way it was installed");

		onOutput($"Upgrading winget package {packageId}...");
		var exitCode = await RunAsync(Winget.CreateStartInfo(false, Winget.UpgradeArgs(packageId)), onOutput, ct);

		return exitCode switch
		{
			0 => new InstallResult(true, $"{tool.DisplayName} updated"),
			Winget.NoApplicableUpgrade => new InstallResult(true, $"{tool.DisplayName} is already up to date"),
			_ => new InstallResult(false, $"winget exited with code 0x{exitCode:X8}")
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
			return new InstallResult(false,
				$"No supported package manager (apt, dnf, pacman) was found. Install {tool.DisplayName} manually");

		List<string> steps = [];
		if (manager.RefreshArgs is not null) steps.Add(string.Join(' ', [manager.Command, .. manager.RefreshArgs]));
		steps.Add(string.Join(' ', [manager.Command, .. upgrade ? manager.UpgradeArgs : manager.InstallArgs, manager.Package(tool)]));

		if (!DependencyChecker.IsCommandAvailable("pkexec"))
			return new InstallResult(false,
				$"Run this in a terminal: {string.Join(" && ", steps.Select(s => "sudo " + s))}");

		var script = string.Join(" && ", steps.Select(s => manager.EnvPrefix + s));
		var exitCode = await RunAsync(ProcessHelper.CreateHidden("pkexec", "sh", "-c", script), onOutput, ct);

		// 126/127: pkexec's own "not authorized" / "dismissed", as opposed to the package manager failing.
		return exitCode switch
		{
			0 => new InstallResult(true, $"{manager.Command} finished"),
			126 or 127 => new InstallResult(false, "Authorization was cancelled or denied"),
			_ => new InstallResult(false, $"{manager.Command} exited with code {exitCode}")
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
		var trimmed = line.Trim();
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
