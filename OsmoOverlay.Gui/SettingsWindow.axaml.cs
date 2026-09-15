using Avalonia.Controls;
using Avalonia.Interactivity;
using OsmoOverlay.Core;

namespace OsmoOverlay.Gui;

public partial class SettingsWindow : Window
{
	public SettingsWindow()
	{
		InitializeComponent();
	}

	public SettingsWindow(int? frameLimit, bool showWatermark) : this()
	{
		FrameLimitBox.Value = frameLimit;
		ShowWatermarkCheck.IsChecked = showWatermark;
	}

	public int? FrameLimit => FrameLimitBox.Value is { } v && v > 0 ? (int)v : null;
	public bool ShowWatermark => ShowWatermarkCheck.IsChecked == true;

	private void OnClearCacheClick(object? sender, RoutedEventArgs e)
	{
		var deleted = FileSummaryReader.ClearCache();
		CacheStatusText.Text = $"Cleared {deleted} cached file(s).";
	}

	private void OnCloseClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}
}
