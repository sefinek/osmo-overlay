using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OsmoOverlay.Core.Dependencies;

namespace OsmoOverlay.Gui;

public partial class DependencyPromptWindow : Window
{
	private readonly IReadOnlyList<ExternalTool> _missing;
	private bool _installing;

	public DependencyPromptWindow()
	{
		InitializeComponent();
		_missing = [];
	}

	public DependencyPromptWindow(IReadOnlyList<ExternalTool> missing)
	{
		InitializeComponent();
		_missing = missing;

		ToolList.ItemsSource = missing.Select(t => t.DisplayName).ToList();

		var canAutoInstall = DependencyInstaller.CanAttemptAutoInstall();
		MessageText.Text = canAutoInstall
			? "OsmoOverlay needs the following tools to work. Install them now?"
			: "OsmoOverlay needs the following tools, but no supported package manager was found. " +
			  "Install them manually, then restart the app.";
		InstallButton.IsVisible = canAutoInstall;

		// Closing mid-install would leave the package manager running with nobody watching its result.
		Closing += (_, e) => e.Cancel |= _installing;
	}

	private void OnSkipClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}

	private async void OnInstallClick(object? sender, RoutedEventArgs e)
	{
		_installing = true;
		InstallButton.IsEnabled = false;
		SkipButton.IsEnabled = false;
		LogPanel.IsVisible = true;

		var allSucceeded = true;
		foreach (ExternalTool tool in _missing)
		{
			AppendLog($"Installing {tool.DisplayName}...");
			InstallResult result;
			try
			{
				result = await DependencyInstaller.InstallAsync(tool, AppendLog, CancellationToken.None);
			}
			catch (Exception ex)
			{
				result = new InstallResult(false, ex.Message);
			}

			AppendLog(result.Message);
			allSucceeded &= result.Success;
		}

		_installing = false;
		AppendLog(allSucceeded ? "Done." : "Some installs failed - see log above.");
		InstallButton.Content = "Retry";
		InstallButton.IsVisible = !allSucceeded;
		InstallButton.IsEnabled = true;
		SkipButton.Content = "Close";
		SkipButton.IsEnabled = true;
	}

	// Dispatcher.UIThread.Post, not a direct call: DependencyInstaller.InstallAsync's onOutput fires
	// from Process.OutputDataReceived, which runs on the process's own async stream-reader thread, not
	// the UI thread - touching LogBox from there throws (Avalonia controls are UI-thread-only).
	private void AppendLog(string message)
	{
		Dispatcher.UIThread.Post(() => LogBox.AppendLog(LogScroll, message));
	}
}
