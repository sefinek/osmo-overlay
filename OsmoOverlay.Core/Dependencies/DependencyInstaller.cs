using System.Diagnostics;

namespace OsmoOverlay.Core.Dependencies;

public sealed record InstallResult(bool Success, string Message);

public static class DependencyInstaller
{
	public static bool CanAttemptAutoInstall()
	{
		if (OperatingSystem.IsWindows()) return DependencyChecker.IsCommandAvailable("winget");
		if (OperatingSystem.IsMacOS()) return DependencyChecker.IsCommandAvailable("brew");
		if (OperatingSystem.IsLinux()) return FindLinuxPackageManager() is not null;
		return false;
	}

	public static async Task<InstallResult> InstallAsync(ExternalTool tool, Action<string> onOutput,
		CancellationToken ct)
	{
		if (OperatingSystem.IsWindows())
			return await RunAsync("winget",
				["install", "--id", tool.WingetId, "-e", "--accept-package-agreements", "--accept-source-agreements"],
				onOutput, ct);

		if (OperatingSystem.IsMacOS())
		{
			if (!DependencyChecker.IsCommandAvailable("brew"))
				return new InstallResult(false,
					"Homebrew is not installed. Install it from https://brew.sh, then retry.");

			return await RunAsync("brew", ["install", tool.BrewPackage], onOutput, ct);
		}

		if (OperatingSystem.IsLinux())
		{
			var manager = FindLinuxPackageManager();
			if (manager is null)
				return new InstallResult(false,
					$"No supported package manager (apt, dnf, pacman) was found. Install {tool.DisplayName} manually.");

			var installArgs = manager switch
			{
				"apt-get" => new[] { "install", "-y", tool.AptPackage },
				"dnf" => new[] { "install", "-y", tool.DnfPackage },
				"pacman" => new[] { "-S", "--noconfirm", tool.PacmanPackage },
				_ => throw new InvalidOperationException($"Unhandled package manager: {manager}")
			};

			if (DependencyChecker.IsCommandAvailable("pkexec"))
				return await RunAsync("pkexec", [manager, .. installArgs], onOutput, ct);

			return new InstallResult(false,
				$"Run this in a terminal to install {tool.DisplayName}: sudo {manager} {string.Join(' ', installArgs)}");
		}

		return new InstallResult(false, "Automatic installation is not supported on this platform.");
	}

	private static string? FindLinuxPackageManager()
	{
		return new[] { "apt-get", "dnf", "pacman" }.FirstOrDefault(DependencyChecker.IsCommandAvailable);
	}

	private static async Task<InstallResult> RunAsync(string command, string[] args, Action<string> onOutput,
		CancellationToken ct)
	{
		var psi = new ProcessStartInfo(command)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden
		};
		foreach (var a in args) psi.ArgumentList.Add(a);

		using Process process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {command}.");

		process.OutputDataReceived += (_, e) =>
		{
			if (e.Data is not null) onOutput(e.Data);
		};
		process.ErrorDataReceived += (_, e) =>
		{
			if (e.Data is not null) onOutput(e.Data);
		};
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		await process.WaitForExitAsync(ct);

		return process.ExitCode == 0
			? new InstallResult(true, $"{command} finished successfully.")
			: new InstallResult(false, $"{command} exited with code {process.ExitCode}.");
	}
}
