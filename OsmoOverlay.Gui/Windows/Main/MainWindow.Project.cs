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

	private static readonly FilePickerFileType ProjectFileType = new(Strings.Project_FileType) { Patterns = [$"*{OverlayProject.Extension}"] };

	private async void OnOpenProjectClick(object? sender, RoutedEventArgs e)
	{
		if (_phase is UiPhase.LoadingSummary or UiPhase.Rendering) return;

		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = Strings.Main_OpenProject,
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

	/// <summary>Ctrl+S saves to the project's file (the Save as dialog while it has none), Ctrl+Shift+S always asks where.</summary>
	private async void SaveProjectFromShortcut(bool saveAs)
	{
		if (_phase is UiPhase.LoadingSummary or UiPhase.Rendering)
		{
			AppendLog(Strings.Project_CantSaveBusy, LogLevel.Warn);
			return;
		}

		if (saveAs || _projectPath is null) await SaveProjectAsAsync();
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
			: _inputPaths.Count > 0
				? Path.GetFileNameWithoutExtension(_inputPaths[0])
				: "project";

		IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = Strings.Project_SaveTitle,
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
			AppLogger.Error(ex, string.Format(Strings.Project_SaveFailed, ex.Message));
			await ConfirmDialog.ShowAsync(this, Strings.Project_SaveFailedTitle, ex.Message, kind: DialogKind.Warning);
			return;
		}

		SetProjectPath(path);
		AppendLog(string.Format(Strings.Project_Saved, path));
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
			preset)
		{
			Moments = loaded ? [.. _moments] : []
		};
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
			AppLogger.Error(ex, string.Format(Strings.Project_OpenFailed, ex.Message));
			await ConfirmDialog.ShowAsync(this, Strings.Project_OpenFailedTitle, ex.Message, kind: DialogKind.Warning);
			return;
		}

		ClosePreview();
		_inputPaths.Clear();
		_inputPaths.AddRange(project.InputPaths);
		RefreshInputFilesList();
		OutputPathBox.Text = string.IsNullOrWhiteSpace(project.OutputPath) ? RenderOptions.DefaultOutputPath(_inputPaths, OverlaySettingsStore.Load().DefaultOutputFolder) : project.OutputPath;
		SetPhase(UiPhase.Idle);
		ActionButton.IsEnabled = _inputPaths.Count > 0;
		SetProjectPath(path);

		List<string> missing = [.. _inputPaths.Where(p => !File.Exists(p))];
		if (_inputPaths.Count == 0 || missing.Count > 0)
		{
			string message = _inputPaths.Count == 0
				? Strings.Project_NoRecordings
				: string.Format(Strings.Project_RecordingsMissing, string.Join('\n', missing));
			await ConfirmDialog.ShowAsync(this, Strings.Project_RecordingsNotFoundTitle, message, kind: DialogKind.Warning);
			return;
		}

		await RunGetSummaryAsync();
		if (_phase != UiPhase.SummaryReady || _summary is null) return;

		AppendLog(string.Format(Strings.Project_Opened, path));
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
				AppLogger.Warn(Strings.Project_CutsRemoveEverything);
			else
			{
				_cuts = cuts;
				ApplyCuts();
			}
		}

		List<KeyMoment> moments = KeyMoments.Normalize(project.Moments, SourceFrames);
		if (moments.Count > 0)
		{
			_moments = moments;
			RefreshMoments();
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
			AppLogger.Warn(ex, Strings.Project_OverlayUnreadable);
			return;
		}

		OverlayPreset? original = _overlayPresets.FirstOrDefault(p => p.Id == project.PresetId);
		if (original is not null && OverlayPresetStore.SameLayout(original, embedded))
		{
			SwitchToPreset(original.Id);
			return;
		}

		string reason = original is null
			? string.Format(Strings.Project_PresetGone, embedded.Name)
			: string.Format(Strings.Project_PresetChanged, original.Name);
		bool useProjects = await ConfirmDialog.AskAsync(this, Strings.Project_OverlayTitle,
			reason + "\n\n" + (original is null ? Strings.Project_UseOverlayGone : Strings.Project_UseOverlayChanged),
			Strings.Project_UseProjects, DialogKind.Info);
		if (!useProjects) return;

		bool idTaken = _overlayPresets.Any(p => p.Id == embedded.Id);
		OverlayPreset added = original is null && !idTaken
			? embedded
			: embedded with
			{
				Id = Guid.NewGuid().ToString("N"),
				Name = original is null ? embedded.Name : string.Format(Strings.Project_PresetCopyName, embedded.Name)
			};
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
		OverlaySettings settings = OverlaySettingsStore.Load();
		if (settings.LastProject != path) OverlaySettingsStore.Save(settings with { LastProject = path });
	}
}
