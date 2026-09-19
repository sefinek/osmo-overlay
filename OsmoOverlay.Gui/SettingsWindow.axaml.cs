using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OsmoOverlay.Gui;

public partial class SettingsWindow : Window
{
	// Width-based (not "480p"/"720p" height labels) since PreviewMaxWidth caps the preview by width
	// (MainWindow.OpenPreviewAsync) - labelling it any other way would need a height that depends on
	// the source's own aspect ratio, which this dialog doesn't know.
	private static readonly List<PreviewQualityOption> PreviewQualityOptions =
	[
		new("Low (640px wide - fastest)", 640),
		new("Medium (960px wide)", 960),
		new("High (1280px wide - default)", 1280),
		new("Very high (1920px wide)", 1920),
		new("Full resolution (native - slowest)", int.MaxValue)
	];

	public SettingsWindow()
	{
		InitializeComponent();
		PreviewQualityCombo.ItemsSource = PreviewQualityOptions;
	}

	public SettingsWindow(int? frameLimit, bool showWatermark, bool smoothGpsMotion, int previewMaxWidth) : this()
	{
		FrameLimitBox.Value = frameLimit;
		ShowWatermarkCheck.IsChecked = showWatermark;
		SmoothGpsMotionCheck.IsChecked = smoothGpsMotion;
		PreviewQualityCombo.SelectedItem =
			PreviewQualityOptions.FirstOrDefault(o => o.MaxWidth == previewMaxWidth) ?? PreviewQualityOptions[2];
	}

	public int? FrameLimit => FrameLimitBox.Value is { } v && v > 0 ? (int)v : null;
	public bool ShowWatermark => ShowWatermarkCheck.IsChecked == true;
	public bool SmoothGpsMotion => SmoothGpsMotionCheck.IsChecked == true;
	public int PreviewMaxWidth => (PreviewQualityCombo.SelectedItem as PreviewQualityOption ?? PreviewQualityOptions[2]).MaxWidth;

	private void OnCloseClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}

	private sealed record PreviewQualityOption(string Display, int MaxWidth)
	{
		public override string ToString()
		{
			return Display;
		}
	}
}
