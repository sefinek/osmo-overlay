using Avalonia.Controls;
using Avalonia.Layout;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Gui;

/// <summary>Shared "Name | installed | latest | [Update]" row layout used by SettingsWindow's About tab.</summary>
internal static class DependencyStatusRows
{
	public static void Populate(Grid grid, IReadOnlyList<ToolVersionInfo> statuses)
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
			AddCell(grid, new TextBlock
			{
				Text = status.LatestVersion is null ? "latest unknown" : $"latest {status.LatestVersion}", Opacity = 0.6
			}, i, 2);

			if (!status.UpdateAvailable) continue;

			var updateButton = new Button { Content = "Update" };
			updateButton.Click += async (_, _) => await OnUpdateClickAsync(status, updateButton);
			AddCell(grid, updateButton, i, 3);
		}
	}

	// AppLogger.Notify (not a local log panel) so the update's line-by-line output always lands in the same place.
	private static async Task OnUpdateClickAsync(ToolVersionInfo status, Button button)
	{
		button.IsEnabled = false;
		AppLogger.Notify($"Updating {status.Tool.DisplayName}...");

		InstallResult result = await DependencyInstaller.UpgradeAsync(status.Tool, AppLogger.Notify, CancellationToken.None);
		AppLogger.Notify(result.Message);

		button.Content = result.Success ? "Updated" : "Retry";
		button.IsEnabled = !result.Success;
	}

	private static void AddCell(Grid grid, Control control, int row, int column)
	{
		control.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetRow(control, row);
		Grid.SetColumn(control, column);
		grid.Children.Add(control);
	}
}
