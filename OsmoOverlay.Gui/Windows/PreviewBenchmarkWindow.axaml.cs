using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace OsmoOverlay.Gui;

public partial class PreviewBenchmarkWindow : Window
{
	public bool UseFullscreen => FullscreenCheck.IsChecked == true;

	public PreviewBenchmarkWindow()
	{
		InitializeComponent();
	}

	public PreviewBenchmarkWindow(bool fullscreen, bool secondScreen, string source) : this()
	{
		FullscreenCheck.IsChecked = fullscreen;
		FullscreenCheck.IsEnabled = !secondScreen;
		DescriptionText.Text = source + Environment.NewLine + Environment.NewLine + Strings.PreviewBenchmark_Description
			+ (secondScreen ? Environment.NewLine + Strings.PreviewBenchmark_SecondScreen : "");
	}

	public PreviewBenchmarkWindow(string report) : this()
	{
		DescriptionText.Text = Strings.PreviewBenchmark_MeasurementNote;
		FullscreenCheck.IsVisible = false;
		StartButton.IsVisible = false;
		ReportScroll.IsVisible = true;
		CopyButton.IsVisible = true;
		ReportText.Text = report;
	}

	private void OnStartClick(object? sender, RoutedEventArgs e) => Close(true);
	private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(false);

	private async void OnCopyClick(object? sender, RoutedEventArgs e)
	{
		if (Clipboard is { } clipboard) await clipboard.SetTextAsync(ReportText.Text ?? "");
	}
}
