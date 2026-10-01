using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     Saving and opening a project (OverlayProject, .ovproj). Opening puts the recordings back and runs Get Summary, then
///     applies what needs a loaded recording: the overlay, the cuts, the 360 view and the position. The overlay is the
///     project's own copy - an unchanged preset is just activated again (so edits keep going to it), a changed or deleted
///     one is the user's call (ApplyProjectPresetAsync).
/// </summary>
public partial class MainWindow
{
	private const string BaseTitle = "OsmoOverlay";

	private string? _projectPath;

	private static readonly FilePickerFileType ProjectFileType = new("OsmoOverlay project") { Patterns = [$"*{OverlayProject.Extension}"] };

	private async void OnOpenProjectClick(object? sender, RoutedEventArgs e)
	{
		if (_phase is UiPhase.LoadingSummary or UiPhase.Rendering) return;

		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Open project",
			AllowMultiple = false,
			FileTypeFilter = [ProjectFileType]
		});
		if (files.Count == 0) return;

		await OpenProjectAsync(files[0].Path.LocalPath);
	}

	private async void OnSaveProjectClick(object? sender, RoutedEventArgs e)
	{
		if (_projectPath is null) await SaveProjectAsAsync();
		else await SaveProjectAsync(_projectPath);
	}

	private async void OnSaveProjectAsClick(object? sender, RoutedEventArgs e)
	{
		await SaveProjectAsAsync();
	}

	private async Task SaveProjectAsAsync()
	{
		string suggested = _projectPath is not null
			? Path.GetFileNameWithoutExtension(_projectPath)
			: _inputPaths.Count > 0 ? Path.GetFileNameWithoutExtension(_inputPaths[0]) : "project";

		IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = "Save project",
			SuggestedFileName = suggested,
			DefaultExtension = OverlayProject.Extension.TrimStart('.'),
			FileTypeChoices = [ProjectFileType]
		});
		if (file is null) return;

		string path = file.Path.LocalPath;
		if (!Path.HasExtension(path)) path += OverlayProject.Extension;
		await SaveProjectAsync(path);
	}

	private async Task SaveProjectAsync(string path)
	{
		try
		{
			BuildProject().Save(path);
		}
		catch (Exception ex)
		{
			AppLogger.Error(ex, $"Could not save the project: {ex.Message}");
			await ConfirmDialog.ShowAsync(this, "Can't save the project", ex.Message, kind: DialogKind.Warning);
			return;
		}

		SetProjectPath(path);
		AppendLog($"Project saved: {path}");
	}

	private OverlayProject BuildProject()
	{
		bool loaded = _summary is not null && _phase is UiPhase.SummaryReady;
		OverlayPreset? preset = _overlayPresetsLoaded ? ActivePreset : null;
		return new OverlayProject(
			loaded ? [.. _summary!.InputPaths] : [.. _inputPaths],
			OutputPathBox.Text,
			loaded ? CutList.Normalize(_cuts, SourceFrames) : [],
			loaded && Is360 ? _reframe : null,
			loaded ? CurrentFrame() : 0,
			preset?.Id,
			preset);
	}

	private async Task OpenProjectAsync(string path)
	{
		OverlayProject project;
		try
		{
			project = OverlayProject.Load(path);
		}
		catch (Exception ex)
		{
			AppLogger.Error(ex, $"Could not open the project: {ex.Message}");
			await ConfirmDialog.ShowAsync(this, "Can't open the project", ex.Message, kind: DialogKind.Warning);
			return;
		}

		ClosePreview();
		_inputPaths.Clear();
		_inputPaths.AddRange(project.InputPaths);
		RefreshInputFilesList();
		OutputPathBox.Text = string.IsNullOrWhiteSpace(project.OutputPath) ? RenderOptions.DefaultOutputPath(_inputPaths) : project.OutputPath;
		SetPhase(UiPhase.Idle);
		ActionButton.IsEnabled = _inputPaths.Count > 0;
		SetProjectPath(path);

		List<string> missing = [.. _inputPaths.Where(p => !File.Exists(p))];
		if (_inputPaths.Count == 0 || missing.Count > 0)
		{
			string message = _inputPaths.Count == 0
				? "The project has no recordings."
				: "These recordings are missing:\n\n" + string.Join('\n', missing) +
				  "\n\nThe project's cuts, view and overlay are applied only after the recordings are found.";
			await ConfirmDialog.ShowAsync(this, "Recordings not found", message, kind: DialogKind.Warning);
			return;
		}

		await RunGetSummaryAsync();
		if (_phase != UiPhase.SummaryReady || _summary is null) return;

		AppendLog($"Project opened: {path}");
		await ApplyProjectPresetAsync(project);
		ApplyProjectEdits(project);
		ResetHistory();
	}

	private void ApplyProjectEdits(OverlayProject project)
	{
		List<FrameRange> cuts = CutList.Normalize(project.Cuts, SourceFrames);
		if (cuts.Count > 0)
		{
			if (CutList.RemovesEverything(cuts, SourceFrames))
				AppLogger.Warn("The project's cuts remove the whole recording - they were not applied");
			else
			{
				_cuts = cuts;
				ApplyCuts();
			}
		}

		if (project.Reframe is { } view && Is360)
		{
			ApplyReframe(view);
			ScheduleThumbnailReload();
		}

		if (_previewFrameSize is not null && project.PreviewFrame > 0) SeekToFrame(project.PreviewFrame);
	}

	private async Task ApplyProjectPresetAsync(OverlayProject project)
	{
		if (project.Preset is null || !_overlayPresetsLoaded) return;

		OverlayPreset embedded;
		try
		{
			embedded = OverlayPresetStore.Sanitize(project.Preset);
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, "The project's overlay is unreadable - the current preset stays");
			return;
		}

		OverlayPreset? original = _overlayPresets.FirstOrDefault(p => p.Id == project.PresetId);
		if (original is not null && OverlayPresetStore.SameLayout(original, embedded))
		{
			SwitchToPreset(original.Id);
			return;
		}

		string reason = original is null
			? $"The preset \"{embedded.Name}\" this project was made with no longer exists."
			: $"The preset \"{original.Name}\" has changed since this project was saved.";
		bool useProjects = await ConfirmDialog.AskAsync(this, "Overlay of the project",
			reason + "\n\nUse the overlay saved in the project? It's added as a preset" +
			(original is null ? "" : " of its own, so the changed one stays as it is") + ".",
			"Use the project's", DialogKind.Info);
		if (!useProjects) return;

		bool idTaken = _overlayPresets.Any(p => p.Id == embedded.Id);
		OverlayPreset added = original is null && !idTaken
			? embedded
			: embedded with { Id = Guid.NewGuid().ToString("N"), Name = original is null ? embedded.Name : $"{embedded.Name} (project)" };
		_overlayPresets.Add(added);
		SwitchToPreset(added.Id);
	}

	private void SwitchToPreset(string id)
	{
		_activePresetId = id;
		RefreshPresetComboBox();
		RefreshWidgetList();
		ShowLayout();
		SaveOverlayPresets();
	}

	private void SetProjectPath(string path)
	{
		_projectPath = path;
		Title = $"{Path.GetFileNameWithoutExtension(path)} - {BaseTitle}";
	}
}
