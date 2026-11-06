using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Telemavix.Core.Logging;
using Telemavix.Core.Updates;

namespace Telemavix.Gui;

/// <summary>The server's switch for old versions (RemoteAccess) at startup: a version that is switched off says so and closes.</summary>
internal static class RemoteAccessFlow
{
	private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

	/// <summary>
	///     A block already seen (kept on disk) holds offline too, but the server is asked first so a lifted one is dropped; otherwise the server is asked in the background, at
	///     start and every few hours (the app may run for days), and the app closes once it answers with one - after a render
	///     in progress, never in the middle of it. True when the app is closing.
	/// </summary>
	public static async Task<bool> EnforceAsync(Window owner, Func<bool> isRendering)
	{
		if (RemoteAccess.Cached().Blocked)
		{
			// The server is asked even so: a block it has lifted since must not stay (CheckAsync falls back to the kept one offline).
			AccessDecision decision = await Task.Run(() => RemoteAccess.CheckAsync(CancellationToken.None));
			if (decision.Blocked)
			{
				await CloseAsync(owner, decision);
				return true;
			}
		}

		_ = WatchLoggedAsync(owner, isRendering);
		return false;
	}

	private static async Task WatchLoggedAsync(Window owner, Func<bool> isRendering)
	{
		try
		{
			await WatchAsync(owner, isRendering);
		}
		catch (Exception ex)
		{
			AppLogger.Error(ex, "Access check failed.");
		}
	}

	private static async Task WatchAsync(Window owner, Func<bool> isRendering)
	{
		using var timer = new PeriodicTimer(Interval);
		do
		{
			AccessDecision decision = await Task.Run(() => RemoteAccess.CheckAsync(CancellationToken.None));
			if (!decision.Blocked) continue;

			while (isRendering()) await Task.Delay(TimeSpan.FromSeconds(30));
			await CloseAsync(owner, decision);
			return;
		} while (await timer.WaitForNextTickAsync());
	}

	private static async Task CloseAsync(Window owner, AccessDecision decision)
	{
		string? url = decision.Url;
		// Over whatever modal is already open (the update prompt, a dialog of the startup): a window with a modal child can't take another.
		while (owner.OwnedWindows.Count > 0) owner = owner.OwnedWindows[^1];
		await ConfirmDialog.ShowAsync(owner, Strings.RemoteAccess_Title,
			string.Format(Strings.RemoteAccess_Message, AppUpdates.CurrentVersionLabel, decision.MinVersion),
			kind: DialogKind.Danger,
			secondaryText: url is null ? null : Strings.RemoteAccess_Open,
			onSecondary: url is null ? null : () => AppUpdateFlow.OpenInBrowser(url),
			extraText: Strings.RemoteAccess_Repository, onExtra: () => AppUpdateFlow.OpenInBrowser(AppUpdates.RepositoryUrl));
		(Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
	}
}
