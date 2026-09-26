using System.Text.Json;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Updates;

namespace OsmoOverlay.Gui;

/// <param name="App">The latest release, null when none is published or the check failed (AppCheckFailed).</param>
/// <param name="Dependencies">Every required tool, installed or not.</param>
internal sealed record UpdateCheckResult(AppRelease? App, bool AppCheckFailed, IReadOnlyList<ToolVersionInfo> Dependencies)
{
	public bool AppUpdateAvailable => App is not null && AppUpdates.IsNewer(App);
}

/// <summary>
///     The one update check - the app's own release and every dependency's latest version, side by side. It runs once at
///     startup and its result is kept: Settings' About tab shows it instead of asking GitHub and the package managers
///     again, and only its "Check for updates" button starts a new one. Used from the UI thread only.
/// </summary>
internal static class UpdateChecks
{
	private static Task<UpdateCheckResult>? _latest;

	/// <summary>The latest check - the one running, or its result; starts one if there's none yet.</summary>
	public static Task<UpdateCheckResult> Latest => _latest ??= RunAsync();

	public static Task<UpdateCheckResult> RefreshAsync()
	{
		return _latest = RunAsync();
	}

	private static async Task<UpdateCheckResult> RunAsync()
	{
		// Off the UI thread: every tool spawns processes (ffmpeg -version, winget/brew/apt-cache), a couple of seconds together.
		Task<(AppRelease?, bool)> app = Task.Run(CheckAppAsync);
		Task<IReadOnlyList<ToolVersionInfo>> dependencies = Task.Run(CheckDependenciesAsync);
		(AppRelease? release, var failed) = await app;
		return new UpdateCheckResult(release, failed, await dependencies);
	}

	private static async Task<(AppRelease?, bool)> CheckAppAsync()
	{
		try
		{
			return (await AppUpdates.GetLatestAsync(CancellationToken.None), false);
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
		{
			AppLogger.Warn(ex, "Could not check for a new version of OsmoOverlay");
			return (null, true);
		}
	}

	private static async Task<IReadOnlyList<ToolVersionInfo>> CheckDependenciesAsync()
	{
		IReadOnlyList<ToolVersionInfo> statuses = await DependencyVersionChecker.CheckAllAsync(RequiredTools.All, CancellationToken.None);
		List<string> updates = [.. statuses.Where(s => s.UpdateAvailable).Select(s => $"{s.Tool.DisplayName} {s.LatestVersion}")];
		if (updates.Count > 0) AppLogger.Notify($"Dependency updates available: {string.Join(", ", updates)} - see Settings > About");
		return statuses;
	}
}
