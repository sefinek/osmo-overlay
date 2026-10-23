using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>Asks which settings preset (ConfigPreset) to work with - shown when a project starts or opens with none chosen and there is a choice.</summary>
internal sealed class ConfigPresetPickerWindow : Window
{
	private readonly ComboBox _list;

	private ConfigPresetPickerWindow(string message, IReadOnlyList<ConfigPreset> presets, string activeId)
	{
		Title = Strings.ConfigPresets_PickTitle;
		Width = 440;
		SizeToContent = SizeToContent.Height;
		CanResize = false;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Background = Palette.WindowBackground;
		Icon = AppAssets.Icon;

		_list = new ComboBox
		{
			ItemsSource = presets.Select(p => new ChoiceOption<string>(p.Name, p.Id)).ToList(),
			HorizontalAlignment = HorizontalAlignment.Stretch
		};
		_list.SelectedIndex = Math.Max(0, presets.ToList().FindIndex(p => p.Id == activeId));

		var choose = new Button { Content = Strings.ConfigPresets_PickConfirm, IsDefault = true };
		choose.Classes.Add("accent");
		choose.Click += (_, _) => Choose();
		var keep = new Button { Content = Strings.ConfigPresets_PickKeep, IsCancel = true };
		keep.Click += (_, _) => Close(null);

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
						new TextBlock { Text = Strings.ConfigPresets_PickTitle, FontSize = 18, FontWeight = Avalonia.Media.FontWeight.Bold },
						new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Palette.TextMuted }
					}
				},
				_list,
				new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { keep, choose } }
			}
		};
	}

	private void Choose()
	{
		Close((_list.SelectedItem as ChoiceOption<string>)?.Value);
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		if (e.Key == Key.Escape) Close(null);
	}

	/// <summary>The id of the preset chosen, null when the dialog was dismissed (the current one stays).</summary>
	public static Task<string?> PickAsync(Window owner, string message, IReadOnlyList<ConfigPreset> presets, string activeId)
	{
		return new ConfigPresetPickerWindow(message, presets, activeId).ShowDialog<string?>(owner);
	}
}
