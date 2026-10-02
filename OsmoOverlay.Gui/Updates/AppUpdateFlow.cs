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
			await ConfirmDialog.ShowAsync(owner, Strings.AppUpdate_Title, Strings.AppUpdate_WhileRendering, kind: DialogKind.Warning);
			return false;
		}

		if (confirm && !await ConfirmDialog.AskAsync(owner, Strings.AppUpdate_Title,
			    string.Format(Strings.AppUpdate_Confirm, release.Version), Strings.Main_Update, DialogKind.Info))
			return false;

		try
		{
			onStatus(string.Format(Strings.AppUpdate_Downloading, release.Version));
			string installer = await AppUpdates.DownloadInstallerAsync(release.Installer!, new Progress<double>(onDownload), CancellationToken.None);
			onStatus(Strings.AppUpdate_StartingInstaller);
			AppUpdates.StartInstaller(installer);
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or Win32Exception)
		{
			AppLogger.Error(ex, Strings.AppUpdate_Failed);
			onStatus(Strings.AppUpdate_Failed);
			await ConfirmDialog.ShowAsync(owner, Strings.AppUpdate_Failed, ex.Message, kind: DialogKind.Danger);
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
			AppLogger.Warn(ex, string.Format(Strings.AppUpdate_BrowserFailed, url));
		}
	}
}
