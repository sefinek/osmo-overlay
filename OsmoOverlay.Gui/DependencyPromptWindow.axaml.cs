using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OsmoOverlay.Core.Dependencies;

namespace OsmoOverlay.Gui;

public partial class DependencyPromptWindow : Window
{
	// Every missing tool with its checkbox - a required one can't be unticked.
	private readonly List<(ExternalTool Tool, CheckBox Check)> _tools = [];
	private bool _installing;

	public DependencyPromptWindow()
	{
		InitializeComponent();
	}

	public DependencyPromptWindow(IReadOnlyList<ExternalTool> missing)
	{
		InitializeComponent();

		var canAutoInstall = DependencyInstaller.CanAttemptAutoInstall();
		foreach (ExternalTool tool in missing.OrderBy(t => t.IsOptional))
		{
			var check = new CheckBox
			{
				Content = tool.IsOptional ? $"{tool.DisplayName} (optional)" : $"{tool.DisplayName} (required)",
				IsChecked = true,
				IsEnabled = tool.IsOptional && canAutoInstall
			};
			_tools.Add((tool, check));
			ToolPanel.Children.Add(check);
		}

		var optionalNote = missing.Any(t => t.IsOptional)
			? " ExifTool is optional - it's only used for cameras whose telemetry OsmoOverlay can't read on its own."
			: "";
		MessageText.Text = canAutoInstall
			? "OsmoOverlay needs the tools below to work. Install them now?" + optionalNote
			: "OsmoOverlay needs the tools below, but no supported package manager was found. " +
			  "Install them manually, then restart the app." + optionalNote;
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

		foreach ((_, CheckBox check) in _tools) check.IsEnabled = false;

		var allSucceeded = true;
		foreach ((ExternalTool tool, _) in _tools.Where(t => t.Check.IsChecked == true))
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
		AppendLog(allSucceeded ? "Done" : "Some installs failed - see log above");
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
