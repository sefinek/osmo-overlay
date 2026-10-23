using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     The Presets page: the settings presets (SettingsPreset) - switching, new, duplicate, delete, rename, export/import and
///     the overlay layout one opens with. A switch first writes what the other pages show into the preset being left (the
///     settings save does it), then loads the next one's values into every page.
/// </summary>
public partial class SettingsWindow
{
	private static readonly ChoiceOption<string?> KeepLayout = new(Strings.SettingsPresets_LayoutKeep, null);

	private string _activeSettingsPresetId = "";

	private bool _loadingSettingsPresets;

	private void LoadSettingsPresets()
	{
		_loadingSettingsPresets = true;
		(List<SettingsPreset> presets, string activeId) = SettingsPresetStore.Load();
		_activeSettingsPresetId = activeId;
		SettingsPresetCombo.ItemsSource = presets.Select(p => new ChoiceOption<string>(p.Name, p.Id)).ToList();
		SettingsPresetCombo.SelectedIndex = presets.FindIndex(p => p.Id == activeId);
		DeleteSettingsPresetButton.IsEnabled = activeId != SettingsPresetStore.DefaultId;

		SettingsPreset active = presets.First(p => p.Id == activeId);
		SettingsPresetNameBox.Text = active.Name;
		PresetGroupHeader.Text = string.Format(Strings.Settings_GroupPreset, active.Name);
		// Default keeps its name and takes no layout of its own.
		ThisPresetLabel.IsVisible = ThisPresetCard.IsVisible = activeId != SettingsPresetStore.DefaultId;
		List<ChoiceOption<string?>> layouts = [KeepLayout, .. OverlayPresetStore.Load().Presets.Select(p => new ChoiceOption<string?>(p.Name, p.Id))];
		PresetLayoutCombo.ItemsSource = layouts;
		PresetLayoutCombo.SelectedItem = layouts.FirstOrDefault(o => o.Value is not null && o.Value == active.LayoutPresetId) ?? KeepLayout;
		_loadingSettingsPresets = false;
	}

	/// <summary>Makes the preset the active one: what the pages show goes into the one left, then every page shows the new one's values.</summary>
	private void SwitchSettingsPreset(string id)
	{
		OverlaySettingsStore.Save(Apply(OverlaySettingsStore.Load()));
		SettingsPreset next = SettingsPresetStore.Load().Presets.First(p => p.Id == id);
		Load(SettingsPresetStore.Activate(next));
	}

	private void OnSettingsPresetChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_loadingSettingsPresets || SettingsPresetCombo.SelectedItem is not ChoiceOption<string> option || option.Value == _activeSettingsPresetId) return;

		SwitchSettingsPreset(option.Value);
	}

	private void OnNewSettingsPresetClick(object? sender, RoutedEventArgs e)
	{
		OverlaySettingsStore.Save(Apply(OverlaySettingsStore.Load()));
		int count = SettingsPresetStore.Load().Presets.Count;
		SwitchSettingsPreset(SettingsPresetStore.Create(string.Format(Strings.SettingsPresets_NewName, count + 1)).Id);
	}

	private void OnDuplicateSettingsPresetClick(object? sender, RoutedEventArgs e)
	{
		OverlaySettingsStore.Save(Apply(OverlaySettingsStore.Load()));
		SettingsPreset source = SettingsPresetStore.Load().Presets.First(p => p.Id == _activeSettingsPresetId);
		SwitchSettingsPreset(SettingsPresetStore.Duplicate(source, string.Format(Strings.SettingsPresets_CopyName, source.Name)).Id);
	}

	private async void OnDeleteSettingsPresetClick(object? sender, RoutedEventArgs e)
	{
		List<SettingsPreset> presets = SettingsPresetStore.Load().Presets;
		if (_activeSettingsPresetId == SettingsPresetStore.DefaultId) return;

		SettingsPreset active = presets.First(p => p.Id == _activeSettingsPresetId);
		bool confirmed = await ConfirmDialog.AskAsync(this, Strings.SettingsPresets_DeleteTitle,
			string.Format(Strings.SettingsPresets_DeleteMessage, active.Name), Strings.Main_Delete, DialogKind.Danger);
		if (!confirmed) return;

		SettingsPresetStore.Delete(active.Id);
		SwitchSettingsPreset(presets.First(p => p.Id != active.Id).Id);
	}

	private async void OnExportSettingsPresetClick(object? sender, RoutedEventArgs e)
	{
		OverlaySettingsStore.Save(Apply(OverlaySettingsStore.Load()));
		SettingsPreset active = SettingsPresetStore.Load().Presets.First(p => p.Id == _activeSettingsPresetId);

		IStorageFile? file = await StorageProvider.SaveRememberedAsync("settings-export", new FilePickerSaveOptions
		{
			Title = Strings.SettingsPresets_ExportTitle,
			SuggestedFileName = active.Name,
			DefaultExtension = "json",
			FileTypeChoices = [new FilePickerFileType(Strings.SettingsPresets_FileType) { Patterns = ["*.json"] }]
		});
		if (file is null) return;

		SettingsPresetStore.ExportToFile(active, file.Path.LocalPath);
		AppLogger.Notify(string.Format(Strings.SettingsPresets_Exported, active.Name, file.Path.LocalPath));
	}

	private async void OnImportSettingsPresetClick(object? sender, RoutedEventArgs e)
	{
		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenRememberedAsync("settings-import", new FilePickerOpenOptions
		{
			Title = Strings.SettingsPresets_ImportTitle,
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType(Strings.SettingsPresets_FileType) { Patterns = ["*.json"] }]
		});
		if (files.Count == 0) return;

		SettingsPreset? imported = SettingsPresetStore.ImportFromFile(files[0].Path.LocalPath);
		if (imported is null)
		{
			AppLogger.Notify(string.Format(Strings.SettingsPresets_ImportFailed, files[0].Path.LocalPath));
			return;
		}

		SwitchSettingsPreset(imported.Id);
		AppLogger.Notify(string.Format(Strings.SettingsPresets_Imported, imported.Name));
	}

	private void OnSettingsPresetNameLostFocus(object? sender, RoutedEventArgs e)
	{
		CommitSettingsPresetName();
	}

	private void OnSettingsPresetNameKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter) CommitSettingsPresetName();
	}

	private void CommitSettingsPresetName()
	{
		if (_loadingSettingsPresets || string.IsNullOrEmpty(_activeSettingsPresetId)) return;

		string name = SettingsPresetNameBox.Text?.Trim() ?? "";
		string current = (SettingsPresetCombo.SelectedItem as ChoiceOption<string>)?.Label ?? "";
		if (name.Length == 0)
		{
			SettingsPresetNameBox.Text = current;
			return;
		}

		if (name == current) return;

		SettingsPresetStore.Modify(_activeSettingsPresetId, p => p with { Name = name });
		LoadSettingsPresets();
	}

	private void OnPresetLayoutChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_loadingSettingsPresets || string.IsNullOrEmpty(_activeSettingsPresetId) || PresetLayoutCombo.SelectedItem is not ChoiceOption<string?> option) return;

		SettingsPresetStore.Modify(_activeSettingsPresetId, p => p with { LayoutPresetId = option.Value });
		if (option.Value is { } layout) OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { ActivePresetId = layout });
	}
}
