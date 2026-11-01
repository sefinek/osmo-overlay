using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OsmoOverlay.Core.Telemetry.External;

namespace OsmoOverlay.Gui;

/// <summary>Asks the size and frame rate of a GPS track viewed with no recording (TrackOnlyRecording) - there is no file to read them from.</summary>
internal sealed class TrackFormatWindow : Window
{
	private static readonly (int Width, int Height)[] Sizes = [(1280, 720), (1920, 1080), (2560, 1440), (3840, 2160), (1080, 1920), (1080, 1080)];
	private static readonly double[] FrameRates = [24, 25, 30, 50, 60];

	private readonly ComboBox _size;
	private readonly ComboBox _fps;

	private TrackFormatWindow(TrackOnlyFormat current)
	{
		Title = Strings.TrackFormat_Title;
		Width = 440;
		SizeToContent = SizeToContent.Height;
		CanResize = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Background = Palette.WindowBackground;
		Icon = AppAssets.Icon;

		List<ChoiceOption<(int, int)>> sizes = [.. Sizes.Select(s => new ChoiceOption<(int, int)>(SizeLabel(s.Width, s.Height), (s.Width, s.Height)))];
		List<ChoiceOption<double>> rates = [.. FrameRates.Select(r => new ChoiceOption<double>(string.Format(Strings.TrackFormat_FpsValue, r), r))];
		_size = new ComboBox
		{
			ItemsSource = sizes,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			SelectedIndex = Math.Max(0, sizes.FindIndex(o => o.Value == (current.Width, current.Height)))
		};
		_fps = new ComboBox
		{
			ItemsSource = rates,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			SelectedIndex = Math.Max(0, rates.FindIndex(o => o.Value == current.Fps))
		};

		var choose = new Button { Content = Strings.TrackFormat_Confirm, IsDefault = true, Classes = { "accent" } };
		choose.Click += (_, _) => Choose();
		var cancel = new Button { Content = Strings.Common_Cancel, IsCancel = true };
		cancel.Click += (_, _) => Close(null);

		Content = new StackPanel
		{
			Margin = new Thickness(24, 20, 24, 20),
			Spacing = 14,
			Children =
			{
				new StackPanel
				{
					Spacing = 4,
					Children =
					{
						new TextBlock { Text = Strings.TrackFormat_Title, FontSize = 18, FontWeight = FontWeight.Bold },
						new TextBlock { Text = Strings.TrackFormat_Message, TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextMuted }
					}
				},
				Labelled(Strings.TrackFormat_Resolution, _size),
				Labelled(Strings.TrackFormat_Fps, _fps),
				new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, choose } }
			}
		};
	}

	private static string SizeLabel(int width, int height)
	{
		return string.Format(width == height ? Strings.TrackFormat_Square : width < height ? Strings.TrackFormat_Vertical : "{0} x {1}", width, height);
	}

	private static StackPanel Labelled(string label, Control control)
	{
		return new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label, Foreground = Palette.TextMuted }, control } };
	}

	private void Choose()
	{
		if (_size.SelectedItem is ChoiceOption<(int, int)> size && _fps.SelectedItem is ChoiceOption<double> fps)
			Close(new TrackOnlyFormat(size.Value.Item1, size.Value.Item2, fps.Value));
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		if (e.Key == Key.Escape) Close(null);
	}

	/// <summary>The format chosen, null when the dialog was dismissed.</summary>
	public static Task<TrackOnlyFormat?> AskAsync(Window owner, TrackOnlyFormat current)
	{
		return new TrackFormatWindow(current).ShowDialog<TrackOnlyFormat?>(owner);
	}
}
