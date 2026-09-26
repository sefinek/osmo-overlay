using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Updates;

namespace OsmoOverlay.Gui;

/// <summary>Updating the app itself (AppUpdates) - shared by Settings' About tab and the check at startup.</summary>
internal static class AppUpdateFlow
{
	/// <summary>
	///     An installed copy (AppUpdates.CanUpdateInPlace): downloads and verifies the setup, starts it and closes the app,
	///     which setup then starts again. Anything else only gets the release page. False when nothing happened.
	/// </summary>
	/// <param name="onDownload">0-1 while the installer downloads, on the UI thread.</param>
	public static async Task<bool> UpdateAsync(Window owner, AppRelease release, Func<bool> isRendering, Action<string> onStatus,
		Action<double> onDownload, bool confirm = true)
	{
		if (!AppUpdates.CanUpdateInPlace(release))
		{
			OpenInBrowser(release.PageUrl);
			return false;
		}

		if (isRendering())
		{
			await ConfirmDialog.ShowAsync(owner, "Update OsmoOverlay",
				"OsmoOverlay has to close to install the update. Let the render finish (or cancel it) first.", kind: DialogKind.Warning);
			return false;
		}

		if (confirm && !await ConfirmDialog.AskAsync(owner, "Update OsmoOverlay",
			    $"OsmoOverlay will download v{release.Version}, close, install it and start again.", "Update", DialogKind.Info))
			return false;

		try
		{
			onStatus($"Downloading OsmoOverlay {release.Version}...");
			var installer = await AppUpdates.DownloadInstallerAsync(release.Installer!, new Progress<double>(onDownload), CancellationToken.None);
			onStatus("Starting the installer...");
			AppUpdates.StartInstaller(installer);
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or Win32Exception)
		{
			AppLogger.Error(ex, "Update failed");
			onStatus("Update failed");
			await ConfirmDialog.ShowAsync(owner, "Update failed", ex.Message, kind: DialogKind.Danger);
			return false;
		}

		(Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
		return true;
	}

	public static void OpenInBrowser(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, $"Could not open {url} in a browser");
		}
	}
}
