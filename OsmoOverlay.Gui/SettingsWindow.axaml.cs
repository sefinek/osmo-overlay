using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OsmoOverlay.Core;

namespace OsmoOverlay.Gui;

public partial class SettingsWindow : Window
{
	// Same "%LocalAppData%\OsmoOverlay" root that FileSummaryCache/OverlayPresetStore/MapTileFetcher/
	// NLog each independently combine for their own subfolder - there's no shared constant for it
	// elsewhere in the repo, so this just mirrors that existing pattern rather than introducing one.
	private static readonly string DataDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay");

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

	private void OnOpenDataFolderClick(object? sender, RoutedEventArgs e)
	{
		try
		{
			Directory.CreateDirectory(DataDir);
			// UseShellExecute so this opens in Explorer (or the platform's file manager) rather than
			// the CreateHidden/redirected-output pattern used elsewhere for ffmpeg/ffprobe/exiftool -
			// the whole point here is to show the folder to the user, not run something silently.
			Process.Start(new ProcessStartInfo(DataDir) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			CacheStatusText.Text = $"Could not open the data folder: {ex.Message}";
		}
	}

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
