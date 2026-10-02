using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     The overlay presets: switching, new, duplicate, delete, reset, export/import and rename, and how they show in the picker.
/// </summary>
public partial class MainWindow
{
	private void OnPresetSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressOverlayEvents || PresetComboBox.SelectedIndex < 0) return;

		_activePresetId = _overlayPresets[PresetComboBox.SelectedIndex].Id;
		RefreshWidgetList();
		ShowLayout();
		SaveOverlayPresets();
	}

	private void OnNewPresetClick(object? sender, RoutedEventArgs e)
	{
		if (_summary is null) return;

		string id = Guid.NewGuid().ToString("N");
		var preset = OverlayPreset.CreateDefault(id, $"Preset {_overlayPresets.Count + 1}");
		_overlayPresets.Add(preset);
		_activePresetId = id;

		RefreshPresetComboBox();
		RefreshWidgetList();
		ShowLayout();
		SaveOverlayPresets();
	}

	private void OnDuplicatePresetClick(object? sender, RoutedEventArgs e)
	{
		OverlayPreset source = _overlayPresets.First(p => p.Id == _activePresetId);
		string id = Guid.NewGuid().ToString("N");
		var copy = new OverlayPreset(id, $"{source.Name} copy", [.. source.Elements]) { Layers = source.Layers?.ToList() };
		_overlayPresets.Add(copy);
		_activePresetId = id;

		RefreshPresetComboBox();
		RefreshWidgetList();
		ShowLayout();
		SaveOverlayPresets();
	}

	private async void OnDeletePresetClick(object? sender, RoutedEventArgs e)
	{
		if (_overlayPresets.Count <= 1)
		{
			AppendLog("Cannot delete the only remaining preset");
			return;
		}

		string presetName = _overlayPresets.FirstOrDefault(p => p.Id == _activePresetId)?.Name ?? "this preset";
		bool confirmed = await ConfirmDialog.AskAsync(this, "Delete preset",
			$"Delete \"{presetName}\"? This can't be undone.", "Delete", DialogKind.Danger);
		if (!confirmed) return;

		_overlayPresets.RemoveAll(p => p.Id == _activePresetId);
		_activePresetId = _overlayPresets[0].Id;

		RefreshPresetComboBox();
		RefreshWidgetList();
		ShowLayout();
		SaveOverlayPresets();
	}

	private async void OnResetPresetClick(object? sender, RoutedEventArgs e)
	{
		if (_summary is null) return;

		int index = _overlayPresets.FindIndex(p => p.Id == _activePresetId);
		if (index < 0) return;

		string presetName = _overlayPresets[index].Name;
		bool confirmed = await ConfirmDialog.AskAsync(this, "Reset preset",
			$"Reset \"{presetName}\" to the default layout? Your widget positions and settings for it will be lost.",
			"Reset", DialogKind.Danger);
		if (!confirmed) return;

		_overlayPresets[index] = OverlayPreset.CreateDefault(_activePresetId, presetName);

		RefreshWidgetList();
		ShowLayout();
		ResetHistory();
		SaveOverlayPresets();
	}

	private async void OnExportPresetClick(object? sender, RoutedEventArgs e)
	{
		if (_overlayPresets.Count == 0) return;
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		OverlayPreset preset = _overlayPresets.First(p => p.Id == _activePresetId);

		IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = "Export overlay preset",
			SuggestedFileName = preset.Name,
			DefaultExtension = "json",
			FileTypeChoices = [new FilePickerFileType("Overlay preset") { Patterns = ["*.json"] }]
		});
		if (file is null) return;

		OverlayPresetStore.ExportToFile(preset, file.Path.LocalPath);
		AppendLog($"Exported preset \"{preset.Name}\" to {file.Path.LocalPath}");
	}

	/// <summary>Same shape as OnDuplicatePresetClick - a new id avoids colliding with a preset already on this machine.</summary>
	private async void OnImportPresetClick(object? sender, RoutedEventArgs e)
	{
		if (_summary is null) return;
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Import overlay preset",
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType("Overlay preset") { Patterns = ["*.json"] }]
		});
		if (files.Count == 0) return;

		OverlayPreset? imported = OverlayPresetStore.ImportFromFile(files[0].Path.LocalPath);
		if (imported is null)
		{
			AppendLog($"Failed to import preset from {files[0].Path.LocalPath} - see the log for details");
			return;
		}

		string id = Guid.NewGuid().ToString("N");
		OverlayPreset preset = imported with { Id = id, CreatedUtc = null, UpdatedUtc = null };
		_overlayPresets.Add(preset);
		_activePresetId = id;

		RefreshPresetComboBox();
		RefreshWidgetList();
		ShowLayout();
		SaveOverlayPresets();
		AppendLog($"Imported preset \"{preset.Name}\"");
	}

	private void OnRenamePresetClick(object? sender, RoutedEventArgs e)
	{
		if (_overlayPresets.Count == 0) return;

		PresetRenameBox.Text = _overlayPresets.First(p => p.Id == _activePresetId).Name;
		PresetComboBox.IsVisible = false;
		PresetRenameBox.IsVisible = true;
		PresetRenameBox.Focus();
		PresetRenameBox.SelectAll();
	}

	private void OnPresetRenameBoxKeyDown(object? sender, KeyEventArgs e)
	{
		switch (e.Key)
		{
			case Key.Enter:
				CommitPresetRename();
				break;
			case Key.Escape:
				CancelPresetRename();
				break;
		}
	}

	private void OnPresetRenameBoxLostFocus(object? sender, RoutedEventArgs e)
	{
		if (PresetRenameBox.IsVisible) CommitPresetRename();
	}

	private void CommitPresetRename()
	{
		string? newName = PresetRenameBox.Text?.Trim();
		if (!string.IsNullOrWhiteSpace(newName))
		{
			int index = _overlayPresets.FindIndex(p => p.Id == _activePresetId);
			if (index >= 0)
			{
				_overlayPresets[index] = _overlayPresets[index] with { Name = newName };
				SaveOverlayPresets();
			}
		}

		CancelPresetRename();
	}

	private void CancelPresetRename()
	{
		PresetRenameBox.IsVisible = false;
		PresetComboBox.IsVisible = true;
		RefreshPresetComboBox();
	}

	/// <summary>When the preset was created and last changed - read when a list item is built, as Save stamps the dates after the list is filled. The built-in Default has none.</summary>
	private string? PresetDatesTooltip(string id)
	{
		if (_overlayPresets.FirstOrDefault(p => p.Id == id) is not { CreatedUtc: { } created, UpdatedUtc: { } updated }) return null;

		return $"Created: {created.ToLocalTime():g}\nLast updated: {updated.ToLocalTime():g}";
	}

	private IDataTemplate PresetItemTemplate()
	{
		return new FuncDataTemplate<PresetOption>((option, _) =>
		{
			var text = new TextBlock { Text = option?.Name, VerticalAlignment = VerticalAlignment.Center };
			if (option is not null) ToolTip.SetTip(text, PresetDatesTooltip(option.Id));
			return text;
		});
	}

	private sealed record PresetOption(string Id, string Name);
}
