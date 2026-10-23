using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     The Presets page: the settings presets (ConfigPreset) - switching, new, duplicate, delete, rename, export/import and
///     the overlay layout one opens with. A switch first writes what the other pages show into the preset being left (the
///     settings save does it), then loads the next one's values into every page.
/// </summary>
public partial class SettingsWindow
{
	private static readonly ChoiceOption<string?> KeepLayout = new(Strings.ConfigPresets_LayoutKeep, null);

	private string _activeConfigPresetId = "";

	private bool _loadingConfigPresets;

	private void LoadConfigPresets()
	{
		_loadingConfigPresets = true;
		(List<ConfigPreset> presets, string activeId) = ConfigPresetStore.Load();
		_activeConfigPresetId = activeId;
		ConfigPresetCombo.ItemsSource = presets.Select(p => new ChoiceOption<string>(p.Name, p.Id)).ToList();
		ConfigPresetCombo.SelectedIndex = presets.FindIndex(p => p.Id == activeId);
		DeleteConfigPresetButton.IsEnabled = activeId != ConfigPresetStore.DefaultId;

		ConfigPreset active = presets.First(p => p.Id == activeId);
		ConfigPresetNameBox.Text = active.Name;
		PresetGroupHeader.Text = string.Format(Strings.Settings_GroupPreset, active.Name);
		// Default keeps its name and takes no layout of its own.
		ThisPresetLabel.IsVisible = ThisPresetCard.IsVisible = activeId != ConfigPresetStore.DefaultId;
		List<ChoiceOption<string?>> layouts = [KeepLayout, .. OverlayPresetStore.Load().Presets.Select(p => new ChoiceOption<string?>(p.Name, p.Id))];
		ConfigLayoutCombo.ItemsSource = layouts;
		ConfigLayoutCombo.SelectedItem = layouts.FirstOrDefault(o => o.Value is not null && o.Value == active.LayoutPresetId) ?? KeepLayout;
		_loadingConfigPresets = false;
	}

	/// <summary>Makes the preset the active one: what the pages show goes into the one left, then every page shows the new one's values.</summary>
	private void SwitchConfigPreset(string id)
	{
		OverlaySettingsStore.Save(Apply(OverlaySettingsStore.Load()));
		ConfigPreset next = ConfigPresetStore.Load().Presets.First(p => p.Id == id);
		Load(ConfigPresetStore.Activate(next));
	}

	private void OnConfigPresetChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_loadingConfigPresets || ConfigPresetCombo.SelectedItem is not ChoiceOption<string> option || option.Value == _activeConfigPresetId) return;

		SwitchConfigPreset(option.Value);
	}

	private void OnNewConfigPresetClick(object? sender, RoutedEventArgs e)
	{
		OverlaySettingsStore.Save(Apply(OverlaySettingsStore.Load()));
		int count = ConfigPresetStore.Load().Presets.Count;
		SwitchConfigPreset(ConfigPresetStore.Create(string.Format(Strings.ConfigPresets_NewName, count + 1)).Id);
	}

	private void OnDuplicateConfigPresetClick(object? sender, RoutedEventArgs e)
	{
		OverlaySettingsStore.Save(Apply(OverlaySettingsStore.Load()));
		ConfigPreset source = ConfigPresetStore.Load().Presets.First(p => p.Id == _activeConfigPresetId);
		SwitchConfigPreset(ConfigPresetStore.Duplicate(source, string.Format(Strings.ConfigPresets_CopyName, source.Name)).Id);
	}

	private async void OnDeleteConfigPresetClick(object? sender, RoutedEventArgs e)
	{
		List<ConfigPreset> presets = ConfigPresetStore.Load().Presets;
		if (_activeConfigPresetId == ConfigPresetStore.DefaultId) return;

		ConfigPreset active = presets.First(p => p.Id == _activeConfigPresetId);
		bool confirmed = await ConfirmDialog.AskAsync(this, Strings.ConfigPresets_DeleteTitle,
			string.Format(Strings.ConfigPresets_DeleteMessage, active.Name), Strings.Main_Delete, DialogKind.Danger);
		if (!confirmed) return;

		ConfigPresetStore.Delete(active.Id);
		SwitchConfigPreset(presets.First(p => p.Id != active.Id).Id);
	}

	private async void OnExportConfigPresetClick(object? sender, RoutedEventArgs e)
	{
		OverlaySettingsStore.Save(Apply(OverlaySettingsStore.Load()));
		ConfigPreset active = ConfigPresetStore.Load().Presets.First(p => p.Id == _activeConfigPresetId);

		IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = Strings.ConfigPresets_ExportTitle,
			SuggestedFileName = active.Name,
			DefaultExtension = "json",
			FileTypeChoices = [new FilePickerFileType(Strings.ConfigPresets_FileType) { Patterns = ["*.json"] }]
		});
		if (file is null) return;

		ConfigPresetStore.ExportToFile(active, file.Path.LocalPath);
		AppLogger.Notify(string.Format(Strings.ConfigPresets_Exported, active.Name, file.Path.LocalPath));
	}

	private async void OnImportConfigPresetClick(object? sender, RoutedEventArgs e)
	{
		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = Strings.ConfigPresets_ImportTitle,
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType(Strings.ConfigPresets_FileType) { Patterns = ["*.json"] }]
		});
		if (files.Count == 0) return;

		ConfigPreset? imported = ConfigPresetStore.ImportFromFile(files[0].Path.LocalPath);
		if (imported is null)
		{
			AppLogger.Notify(string.Format(Strings.ConfigPresets_ImportFailed, files[0].Path.LocalPath));
			return;
		}

		SwitchConfigPreset(imported.Id);
		AppLogger.Notify(string.Format(Strings.ConfigPresets_Imported, imported.Name));
	}

	private void OnConfigPresetNameLostFocus(object? sender, RoutedEventArgs e)
	{
		CommitConfigPresetName();
	}

	private void OnConfigPresetNameKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter) CommitConfigPresetName();
	}

	private void CommitConfigPresetName()
	{
		if (_loadingConfigPresets || string.IsNullOrEmpty(_activeConfigPresetId)) return;

		string name = ConfigPresetNameBox.Text?.Trim() ?? "";
		string current = (ConfigPresetCombo.SelectedItem as ChoiceOption<string>)?.Label ?? "";
		if (name.Length == 0)
		{
			ConfigPresetNameBox.Text = current;
			return;
		}

		if (name == current) return;

		ConfigPresetStore.Modify(_activeConfigPresetId, p => p with { Name = name });
		LoadConfigPresets();
	}

	private void OnConfigLayoutChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_loadingConfigPresets || string.IsNullOrEmpty(_activeConfigPresetId) || ConfigLayoutCombo.SelectedItem is not ChoiceOption<string?> option) return;

		ConfigPresetStore.Modify(_activeConfigPresetId, p => p with { LayoutPresetId = option.Value });
		if (option.Value is { } layout) OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { ActivePresetId = layout });
	}
}
