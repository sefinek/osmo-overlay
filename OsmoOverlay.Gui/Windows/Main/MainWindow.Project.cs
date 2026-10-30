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

	private DateTime? _savedAtTitle;

	private static readonly FilePickerFileType ProjectFileType = new(Strings.Project_FileType) { Patterns = [$"*{OverlayProject.Extension}"] };

	private async void OnNewProjectClick(object? sender, RoutedEventArgs e)
	{
		if (_phase is UiPhase.LoadingSummary or UiPhase.Rendering || _previewTestRunning) return;

		bool hasWork = _inputPaths.Count > 0 || _projectPath is not null;
		bool askedAboutChanges = HasUnsavedChanges;
		if (!await ConfirmUnsavedAsync()) return;
		if (hasWork && !askedAboutChanges && !await ConfirmDialog.AskAsync(this, Strings.Project_NewTitle, Strings.Project_NewMessage, Strings.Main_NewProject, DialogKind.Warning)) return;

		StartNewProject();
		await ChooseSettingsPresetAsync(Strings.SettingsPresets_PickNew);
	}

	/// <summary>
	///     An empty session: no recordings, output, cuts, key moments or 360 view, and no project file - the one that was open
	///     is left as it was on disk. What isn't the project's (settings, presets, the interface) stays.
	/// </summary>
	private void StartNewProject()
	{
		ClearExternalTrack();
		ClosePreview();
		ResetReframe();
		ClearCuts();
		SetVolumeLine([]);
		ResetMusic();
		ClearMoments();
		ReleaseTimelineTracks();
		_inputPaths.Clear();
		RefreshInputFilesList();
		OutputPathBox.Text = "";
		SetPhase(UiPhase.Idle);
		ActionButton.IsEnabled = false;
		_projectPath = null;
		_savedAtTitle = null;
		_cleanFingerprint = null;
		SetUnsaved(false);
		UpdateTitle();
		ResetHistory();
	}

	private async void OnOpenProjectClick(object? sender, RoutedEventArgs e)
	{
		if (_phase is UiPhase.LoadingSummary or UiPhase.Rendering) return;

		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenRememberedAsync("project-open", new FilePickerOpenOptions
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
		await SaveProjectCoreAsync();
	}

	/// <summary>Saves to the project's file, or asks where while it has none.</summary>
	private async Task SaveProjectCoreAsync()
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

		IStorageFile? file = await StorageProvider.SaveRememberedAsync("project-save", new FilePickerSaveOptions
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
		OverlayProject project;
		try
		{
			// Built here (it reads the window), written off the UI thread - the pointer is the busy one for as long as that takes, over
			// a shield across the window as many controls set a cursor of their own (the timelines, buttons).
			project = BuildProject();
			SaveCursorShield.IsVisible = true;
			await Task.Run(() => project.Save(path));
		}
		catch (Exception ex)
		{
			SaveCursorShield.IsVisible = false;
			AppLogger.Error(ex, string.Format(Strings.Project_SaveFailed, ex.Message));
			await ConfirmDialog.ShowAsync(this, Strings.Project_SaveFailedTitle, ex.Message, kind: DialogKind.Warning);
			return;
		}

		SaveCursorShield.IsVisible = false;
		SetProjectPath(path, DateTime.Now);
		MarkClean(project);
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
			Moments = loaded ? [.. _moments] : [],
			Volume = loaded ? [.. _volumeLine] : [],
			Music = loaded ? [.. _music] : [],
			ExternalTrack = _externalTrack,
			SettingsPresetId = OverlaySettingsStore.Load().ActiveSettingsPresetId
		};
	}

	private async Task OpenProjectAsync(string path)
	{
		if (!await ConfirmUnsavedAsync()) return;

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
		await ApplyProjectSettingsPresetAsync(project);
		_cameraSummary = null;
		_externalTrackApplied = false;
		_externalTrack = project.ExternalTrack;
		RefreshExternalTrack();
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
		MarkClean();
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

		SetVolumeLine(project.Volume ?? []);

		List<MusicClip> music = MusicTrack.Normalize(project.Music ?? [], OutputSeconds);
		List<string> missingMusic = [.. music.Where(c => !File.Exists(c.Path)).Select(c => Path.GetFileName(c.Path)).Distinct()];
		if (missingMusic.Count > 0) AppLogger.Warn(string.Format(Strings.Project_MusicMissing, string.Join(", ", missingMusic)));
		SetMusic(music.Where(c => File.Exists(c.Path)));

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

	/// <summary>
	///     The project's settings preset is made the active one, before the recordings load so they open with its values. A project
	///     with none (or whose preset is gone) asks, when there is a choice.
	/// </summary>
	private async Task ApplyProjectSettingsPresetAsync(OverlayProject project)
	{
		(List<SettingsPreset> presets, string activeId) = SettingsPresetStore.Load();
		if (presets.Count < 2) return;

		if (presets.FirstOrDefault(p => p.Id == project.SettingsPresetId) is { } own)
		{
			if (own.Id != activeId) AdoptSettings(SettingsPresetStore.Activate(own));
			return;
		}

		await ChooseSettingsPresetAsync(Strings.SettingsPresets_PickProject);
	}

	/// <summary>Asks which settings preset to work with (nothing to ask with a single one); dismissing keeps the current.</summary>
	private async Task ChooseSettingsPresetAsync(string message)
	{
		(List<SettingsPreset> presets, string activeId) = SettingsPresetStore.Load();
		if (presets.Count < 2) return;

		string? chosen = await SettingsPresetPickerWindow.PickAsync(this, message, presets, activeId);
		if (chosen is null || chosen == activeId || presets.FirstOrDefault(p => p.Id == chosen) is not { } preset) return;

		AdoptSettings(SettingsPresetStore.Activate(preset));
	}

	/// <summary>What the window keeps from settings.json for itself, brought up to date after a preset switch: watermark, GPS smoothing and the overlay layout.</summary>
	private void AdoptSettings(OverlaySettings settings)
	{
		_smoothGpsMotion = settings.SmoothGpsMotion;
		if (settings.ShowWatermark != _showWatermark)
		{
			_showWatermark = settings.ShowWatermark;
			_previewPlayer.SetShowWatermark(_showWatermark);
		}

		AdoptLayout(settings);
		UpdateTitle();
	}

	/// <summary>A settings preset opens with its own overlay layout - the window's list is kept in step with the one settings.json names.</summary>
	private void AdoptLayout(OverlaySettings settings)
	{
		if (settings.ActivePresetId is not { } layoutId || layoutId == _activePresetId || _overlayPresets.All(p => p.Id != layoutId)) return;

		SwitchToPreset(layoutId);
	}

	/// <summary>
	///     A built-in layout follows the recording's camera (a drone's footage gets the drone layout, anything else the default one);
	///     a layout of the user's own is left alone. A project opened after this brings its own.
	/// </summary>
	private void AdoptCameraLayout(FileSummary summary)
	{
		string wanted = summary.CameraFormat?.PreferredPresetId ?? OverlayPresetStore.DefaultPresetId;
		if (!OverlayPresetStore.IsBuiltIn(_activePresetId) || _activePresetId == wanted || _overlayPresets.All(p => p.Id != wanted)) return;
		if (OverlayPresetStore.IsDronePreset(_activePresetId) && OverlayPresetStore.IsDronePreset(wanted)) return;

		SwitchToPreset(wanted);
	}

	private void SwitchToPreset(string id)
	{
		_activePresetId = id;
		RefreshPresetComboBox();
		RefreshWidgetList();
		ShowLayout();
		SaveOverlayPresets();
	}

	/// <summary>
	///     The window's title: the project's name (when it has a file) and the program, then the settings preset once a recording is
	///     loaded - and only while there is more than one, as a single preset has nothing to tell apart. A save adds when it happened.
	/// </summary>
	private void UpdateTitle()
	{
		string star = HasUnsavedChanges ? "*" : "";
		string title = _projectPath is { } path ? $"{Path.GetFileNameWithoutExtension(path)}{star} - {BaseTitle}" : BaseTitle + star;
		if (_phase is UiPhase.SummaryReady or UiPhase.Rendering)
		{
			(List<SettingsPreset> presets, string activeId) = SettingsPresetStore.Load();
			if (presets.Count > 1 && presets.FirstOrDefault(p => p.Id == activeId) is { } active) title += $" | {active.Name}";
		}

		if (_savedAtTitle is { } at) title += $" ({string.Format(Strings.Project_SavedAtTitle, at.ToString("T"))})";
		Title = title;
	}

	/// <param name="savedAt">When it was saved - the title says so, which is how a save shows with the log out of sight.</param>
	private void SetProjectPath(string path, DateTime? savedAt = null)
	{
		_projectPath = path;
		_savedAtTitle = savedAt;
		UpdateTitle();
		OverlaySettings settings = OverlaySettingsStore.Load();
		if (settings.LastProject != path) OverlaySettingsStore.Save(settings with { LastProject = path });
	}
}
