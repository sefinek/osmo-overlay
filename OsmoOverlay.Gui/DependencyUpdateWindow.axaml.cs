using Avalonia.Controls;
using Avalonia.Interactivity;
using OsmoOverlay.Core.Dependencies;

namespace OsmoOverlay.Gui;

public partial class DependencyUpdateWindow : Window
{
	public DependencyUpdateWindow()
	{
		InitializeComponent();
		_ = InitializeAsync();
	}

	private async Task InitializeAsync()
	{
		// Off the UI thread: each tool spawns a process (ffmpeg -version, winget/brew/apt-cache show),
		// which can take a couple of seconds combined. Per-tool progress reaches the main window's LOG
		// panel via AppLogger.Notify (see DependencyVersionChecker.CheckAsync); MessageText only shows
		// "Checking..." then the final one-line verdict, so it doesn't flicker through the same detail
		// the table below already lays out per tool.
		IReadOnlyList<ToolVersionInfo> statuses =
			await Task.Run(() => DependencyVersionChecker.CheckAllAsync(RequiredTools.All, CancellationToken.None));

		List<ToolVersionInfo> present = [.. statuses.Where(s => s.InstalledVersion is not null)];
		if (present.Count == 0)
		{
			MessageText.Text = "None of the required tools were found - install them from the main window first.";
		}
		else
		{
			var outdated = present.Count(s => s.UpdateAvailable);
			MessageText.Text = outdated == 0
				? "Everything is up to date."
				: $"{outdated} of {present.Count} tool(s) have an update available.";
		}

		DependencyStatusRows.Populate(ToolsGrid, present);
	}

	private void OnCloseClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}
}
