using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OsmoOverlay.Gui;

public partial class SettingsWindow : Window
{
	public SettingsWindow()
	{
		InitializeComponent();
	}

	public SettingsWindow(int? frameLimit, bool showWatermark, bool smoothGpsMotion) : this()
	{
		FrameLimitBox.Value = frameLimit;
		ShowWatermarkCheck.IsChecked = showWatermark;
		SmoothGpsMotionCheck.IsChecked = smoothGpsMotion;
	}

	public int? FrameLimit => FrameLimitBox.Value is { } v && v > 0 ? (int)v : null;
	public bool ShowWatermark => ShowWatermarkCheck.IsChecked == true;
	public bool SmoothGpsMotion => SmoothGpsMotionCheck.IsChecked == true;

	private void OnCloseClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}
}
