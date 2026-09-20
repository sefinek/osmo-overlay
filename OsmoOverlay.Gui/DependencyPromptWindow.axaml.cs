using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OsmoOverlay.Core.Dependencies;

namespace OsmoOverlay.Gui;

public partial class DependencyPromptWindow : Window
{
	private readonly IReadOnlyList<ExternalTool> _missing;

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
		MessageText.Text = "Checking for a package manager...";
		InstallButton.IsVisible = false;

		_ = InitializeAsync();
	}

	private async Task InitializeAsync()
	{
		// Checking for winget/brew/apt launches a process and can block for a few seconds,
		// so it runs off the UI thread instead of in the constructor.
		var canAutoInstall = await Task.Run(DependencyInstaller.CanAttemptAutoInstall);

		MessageText.Text = canAutoInstall
			? "OsmoOverlay needs the following tools to work. Install them now?"
			: "OsmoOverlay needs the following tools, but no supported package manager was found. " +
			  "Install them manually, then restart the app.";
		InstallButton.IsVisible = canAutoInstall;
	}

	private void OnSkipClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}

	private async void OnInstallClick(object? sender, RoutedEventArgs e)
	{
		InstallButton.IsEnabled = false;
		SkipButton.IsEnabled = false;
		LogPanel.IsVisible = true;

		var allSucceeded = true;
		foreach (ExternalTool tool in _missing)
		{
			AppendLog($"Installing {tool.DisplayName}...");
			InstallResult result = await DependencyInstaller.InstallAsync(tool, AppendLog, CancellationToken.None);
			AppendLog(result.Message);
			allSucceeded &= result.Success;
		}

		AppendLog(allSucceeded ? "Done. Restart the app if a tool still isn't detected." : "Some installs failed - see log above.");
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
