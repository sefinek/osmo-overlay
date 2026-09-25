using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Gui;

/// <summary>Shared "Name | installed | latest | [Update]" row layout used by SettingsWindow's About tab.</summary>
internal static class DependencyStatusRows
{
	/// <summary>The same rows before the first check is back, so the layout doesn't jump when the versions arrive.</summary>
	public static void ShowChecking(Grid grid, IReadOnlyList<ExternalTool> tools)
	{
		grid.RowDefinitions.Clear();
		grid.Children.Clear();

		for (var i = 0; i < tools.Count; i++)
		{
			grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
			AddCell(grid, new TextBlock { Text = tools[i].DisplayName }, i, 0);
			AddCell(grid, new TextBlock { Text = "installed ...", Opacity = 0.6 }, i, 1);
			AddCell(grid, new TextBlock { Text = "latest ...", Opacity = 0.6 }, i, 2);
		}
	}

	/// <param name="isRendering">An FFmpeg update that restarts the app waits until this is false.</param>
	public static void Populate(Window owner, Grid grid, IReadOnlyList<ToolVersionInfo> statuses, Func<bool> isRendering)
	{
		grid.RowDefinitions.Clear();
		grid.Children.Clear();

		for (var i = 0; i < statuses.Count; i++)
		{
			grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
			ToolVersionInfo status = statuses[i];

			AddCell(grid, new TextBlock { Text = status.Tool.DisplayName }, i, 0);
			AddCell(grid, new TextBlock
			{
				Text = status.InstalledVersion is null ? "not found" : $"installed {status.InstalledVersion}", Opacity = 0.6
			}, i, 1);
			AddCell(grid, new TextBlock { Text = LatestText(status), Opacity = 0.6 }, i, 2);

			if (!status.UpdateAvailable) continue;

			var updateButton = new Button { Content = "Update" };
			updateButton.Click += async (_, _) => await OnUpdateClickAsync(owner, status, updateButton, isRendering);
			AddCell(grid, updateButton, i, 3);
		}
	}

	private static string LatestText(ToolVersionInfo status)
	{
		return (status.LatestVersion, status.UnsupportedVersion) switch
		{
			(null, null) => "latest unknown",
			({ } latest, null) => $"latest {latest}",
			(null, { } unsupported) => $"latest {unsupported} (not supported yet)",
			({ } latest, { } unsupported) => $"latest {latest} ({unsupported} not supported yet)"
		};
	}

	// AppLogger.Notify (not a local log panel) so the update's line-by-line output always lands in the same place.
	private static async Task OnUpdateClickAsync(Window owner, ToolVersionInfo status, Button button, Func<bool> isRendering)
	{
		if (DependencyInstaller.UpgradeNeedsRestart(status.Tool))
		{
			await UpdateAfterRestartAsync(owner, status, button, isRendering);
			return;
		}

		button.IsEnabled = false;
		AppLogger.Notify($"Updating {status.Tool.DisplayName}...");

		InstallResult result;
		try
		{
			result = await DependencyInstaller.UpgradeAsync(status.Tool, AppLogger.Notify, CancellationToken.None);
		}
		catch (Exception ex)
		{
			result = new InstallResult(false, ex.Message);
		}

		AppLogger.Notify(result.Message);

		button.Content = result.Success ? "Updated" : "Retry";
		button.IsEnabled = !result.Success;
	}

	/// <summary>The preview has FFmpeg's DLLs loaded, and Windows won't let winget replace them - see DependencyInstaller.UpgradeNeedsRestart.</summary>
	private static async Task UpdateAfterRestartAsync(Window owner, ToolVersionInfo status, Button button, Func<bool> isRendering)
	{
		var name = status.Tool.DisplayName;
		if (isRendering())
		{
			await ConfirmDialog.ShowAsync(owner, $"Update {name}",
				$"OsmoOverlay has to close to update {name}. Let the render finish (or cancel it) first.", kind: DialogKind.Warning);
			return;
		}

		var confirmed = await ConfirmDialog.AskAsync(owner, $"Update {name}",
			$"The preview keeps {name}'s libraries in use, so OsmoOverlay will close, update {name} to {status.LatestVersion} " +
			"in a separate window and then start again.", "Close and update", DialogKind.Warning);
		if (!confirmed) return;

		button.IsEnabled = false;
		InstallResult result;
		try
		{
			result = await DependencyInstaller.StartUpgradeAfterExitAsync(status.Tool, CancellationToken.None);
		}
		catch (Exception ex)
		{
			result = new InstallResult(false, ex.Message);
		}

		AppLogger.Notify(result.Message);
		if (!result.Success)
		{
			button.Content = "Retry";
			button.IsEnabled = true;
			return;
		}

		(Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
	}

	private static void AddCell(Grid grid, Control control, int row, int column)
	{
		control.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetRow(control, row);
		Grid.SetColumn(control, column);
		grid.Children.Add(control);
	}
}
