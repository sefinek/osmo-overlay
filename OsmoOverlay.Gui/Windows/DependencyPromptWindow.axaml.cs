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

		bool canAutoInstall = DependencyInstaller.CanAttemptAutoInstall();
		foreach (ExternalTool tool in missing.OrderBy(t => t.IsOptional))
		{
			var check = new CheckBox
			{
				Content = string.Format(tool.IsOptional ? Strings.DependencyPrompt_ToolOptional : Strings.DependencyPrompt_ToolRequired, tool.DisplayName),
				IsChecked = true,
				IsEnabled = tool.IsOptional && canAutoInstall
			};
			_tools.Add((tool, check));
			ToolPanel.Children.Add(check);
		}

		string optionalNote = missing.Any(t => t.IsOptional)
			? " " + Strings.DependencyPrompt_ExifToolOptional
			: "";
		MessageText.Text = (canAutoInstall ? Strings.DependencyPrompt_InstallNow : Strings.DependencyPrompt_InstallManually) + optionalNote;
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

		bool allSucceeded = true;
		foreach ((ExternalTool tool, _) in _tools.Where(t => t.Check.IsChecked == true))
		{
			AppendLog(string.Format(Strings.Welcome_InstallingTool, tool.DisplayName));
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
		AppendLog(allSucceeded ? Strings.DependencyPrompt_Done : Strings.DependencyPrompt_SomeFailed);
		InstallButton.Content = Strings.Welcome_Retry;
		InstallButton.IsVisible = !allSucceeded;
		InstallButton.IsEnabled = true;
		SkipButton.Content = Strings.Tools_Close;
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
