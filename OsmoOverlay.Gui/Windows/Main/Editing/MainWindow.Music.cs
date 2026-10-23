using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Gui;

/// <summary>
///     The music on the timeline (MusicClip, shown by MusicTracks): files added from the track's menu or its "+", moved and
///     trimmed by dragging, the rest set in the clip's flyout. It is part of the undo history and of the project, the preview
///     mixes it (PreviewPlayer.SetMusic) and the render does (RenderOptions.Music). Timed on the output, so a cut doesn't
///     take it out; the edits are kept as they are and cut down to the output where they are played (MusicTrack.Normalize).
/// </summary>
public partial class MainWindow
{
	private static readonly DataFormat<string> MusicDragFormat = DataFormat.CreateInProcessFormat<string>("OsmoOverlay.Music");

	private List<MusicClip> _music = [];
	private bool _musicTrackShown;
	private readonly Dictionary<string, AudioWaveform?> _musicWaveforms = new(StringComparer.OrdinalIgnoreCase);
	private (PointerPressedEventArgs Pressed, bool Started)? _musicDragStart;

	private double OutputSeconds => _summary is { Video.Fps: > 0 } summary ? PlannedFrameCount() / summary.Video.Fps : 0;

	private void WireMusic()
	{
		MusicTracks.Source = ExpandedTimeline;
		MusicTracks.Waveform = path => _musicWaveforms.GetValueOrDefault(path);
		MusicTracks.ClipsEdited += OnMusicEdited;
		MusicTracks.MenuRequested += ShowMusicMenu;
		MusicTracks.EditRequested += ShowMusicEditor;
		MusicTracks.AddRequested += () => _ = AddMusicAsync(null);
		MusicTracks.SeekRequested += seconds => PreviewTimeline.Value = seconds;
		MusicTracks.ScrubStarted += BeginTimelineScrub;
		MusicTracks.ScrubEnded += EndTimelineScrub;
	}

	/// <summary>The track's mapping between the timeline's recording seconds and the output's, in step with the cuts.</summary>
	private void RefreshMusicView()
	{
		double fps = _summary is { Video.Fps: > 0 } summary ? summary.Video.Fps : 30;
		MusicTracks.Fps = fps;
		MusicTracks.OutputDurationSeconds = OutputSeconds;
		if (_outputTimeline is { } output)
		{
			MusicTracks.ToRecording = output.ToRecordingSeconds;
			MusicTracks.ToOutput = output.NearestOutputSeconds;
		}
		else
		{
			MusicTracks.ToRecording = seconds => seconds;
			MusicTracks.ToOutput = seconds => seconds;
		}

		MusicTracks.Clips = _music;
	}

	private void ShowMusic()
	{
		if (_music.Count > 0) _musicTrackShown = true;
		MusicTracks.IsVisible = _musicTrackShown;
		MusicTracks.Clips = _music;
		foreach (MusicClip clip in _music) _ = LoadMusicWaveformAsync(clip);
		_previewPlayer.SetMusic(_music);
	}

	/// <summary>A new recording or project: no music, and the track is hidden again until the palette's "Music" is dragged onto the timeline.</summary>
	private void ResetMusic()
	{
		_musicTrackShown = false;
		foreach (AudioWaveform? waveform in _musicWaveforms.Values) waveform?.Dispose();
		_musicWaveforms.Clear();
		SetMusic([]);
	}

	/// <summary>The file's peaks for the clip, decoded in the background (and cached like the recording's); the track redraws as they come in.</summary>
	private async Task LoadMusicWaveformAsync(MusicClip clip)
	{
		if (_musicWaveforms.ContainsKey(clip.Path)) return;

		_musicWaveforms[clip.Path] = null;
		AudioWaveform? waveform = null;
		try
		{
			waveform = await Task.Run(() => AudioWaveform.Start([new PlaybackSegment(clip.Path, clip.SourceSeconds, 0)], true));
		}
		catch (Exception ex) when (ex is InvalidOperationException or IOException)
		{
			AppLogger.Warn(ex, string.Format(Strings.Music_CantUse, Path.GetFileName(clip.Path), ex.Message));
		}

		if (waveform is null) return;

		if (!_musicWaveforms.ContainsKey(clip.Path))
		{
			waveform.Dispose();
			return;
		}

		_musicWaveforms[clip.Path] = waveform;
		waveform.Updated += () => Dispatcher.UIThread.Post(MusicTracks.InvalidateVisual);
		MusicTracks.InvalidateVisual();
	}

	private void OnMusicItemPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (_summary is null || !e.GetCurrentPoint((Control)sender!).Properties.IsLeftButtonPressed) return;

		_musicDragStart = (e, false);
	}

	private async void OnMusicItemPointerMoved(object? sender, PointerEventArgs e)
	{
		if (_musicDragStart is not var (pressed, _) || sender is not Control item) return;
		if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed)
		{
			_musicDragStart = null;
			return;
		}

		Point delta = e.GetPosition(item) - pressed.GetPosition(item);
		if (Math.Abs(delta.X) < DragDropThreshold && Math.Abs(delta.Y) < DragDropThreshold) return;

		_musicDragStart = null;
		var data = new DataTransfer();
		data.Add(DataTransferItem.Create(MusicDragFormat, "music"));
		await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move);
	}

	private void OnMusicItemPointerEnded(object? sender, RoutedEventArgs e)
	{
		_musicDragStart = null;
	}

	private void OnMusicDragOver(object? sender, DragEventArgs e)
	{
		e.DragEffects = _summary is not null && e.DataTransfer.TryGetValue(MusicDragFormat) is not null ? DragDropEffects.Move : DragDropEffects.None;
	}

	/// <summary>The palette's "Music" dropped on the timeline: the track shows up and asks for the file, to start where it was dropped.</summary>
	private void OnMusicDrop(object? sender, DragEventArgs e)
	{
		if (_summary is null || e.DataTransfer.TryGetValue(MusicDragFormat) is null) return;

		_musicTrackShown = true;
		ShowMusic();
		Point at = e.GetPosition(ExpandedTimeline);
		double? start = at.X >= 0 && at.X <= ExpandedTimeline.Bounds.Width
			? _outputTimeline?.NearestOutputSeconds(ExpandedTimeline.SecondsAt(at.X)) ?? ExpandedTimeline.SecondsAt(at.X)
			: null;
		_ = AddMusicAsync(start);
	}

	private void SetMusic(IEnumerable<MusicClip> clips)
	{
		_music = [.. clips];
		ShowMusic();
	}

	/// <summary>Live while dragging (the preview follows), kept in order and in the history on release.</summary>
	private void OnMusicEdited(IReadOnlyList<MusicClip> clips, bool final)
	{
		if (!final)
		{
			_music = [.. clips];
			ShowMusic();
			CommitHistory("music");
			return;
		}

		SetMusicSorted(clips);
		CommitHistory("music");
		SealHistory();
	}

	private void SetMusicSorted(IEnumerable<MusicClip> clips, MusicClip? select = null)
	{
		_music = MusicTrack.Normalize(clips, Math.Max(OutputSeconds, 0));
		ShowMusic();
		int selected = select is null ? -1 : _music.IndexOf(select);
		MusicTracks.SelectedIndex = selected >= 0 ? selected : null;
	}

	private async Task AddMusicAsync(double? outputSeconds)
	{
		if (_summary is null) return;

		TopLevel topLevel = GetTopLevel(this) ?? this;
		IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenRememberedAsync("music", new FilePickerOpenOptions
		{
			Title = Strings.Music_PickTitle,
			FileTypeFilter = [new FilePickerFileType(Strings.Music_FileType) { Patterns = ["*.mp3", "*.m4a", "*.aac", "*.wav", "*.flac", "*.ogg", "*.opus", "*.wma", "*.aiff", "*.mp4", "*.mkv", "*.mov"] }]
		});
		if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

		double sourceSeconds;
		try
		{
			sourceSeconds = await Task.Run(() => SourceProbe.ProbeAudioDuration(path));
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, string.Format(Strings.Music_CantUse, Path.GetFileName(path), ex.Message));
			await ConfirmDialog.ShowAsync(this, Strings.Music_CantUseTitle, string.Format(Strings.Music_CantUse, Path.GetFileName(path), ex.Message),
				kind: DialogKind.Warning);
			return;
		}

		double output = OutputSeconds;
		double start = outputSeconds ?? _outputTimeline?.NearestOutputSeconds(PreviewTimeline.Value) ?? PreviewTimeline.Value;
		start = Math.Clamp(start, 0, Math.Max(0, output - 1));
		double length = Math.Min(sourceSeconds, output - start);
		var clip = new MusicClip(path, start, 0, length, sourceSeconds);
		SetMusicSorted([.. _music, clip], clip);
		CommitHistory("music");
		SealHistory();
		AppendLog(string.Format(Strings.Music_Added, Path.GetFileName(path)));
		if (!_previewPlayer.HasAudio) AppendLog(Strings.Music_NoPreviewSound);
	}

	private void ShowMusicMenu(int? index, double outputSeconds)
	{
		var items = new List<MenuItem>();
		var add = new MenuItem { Header = Strings.Music_Add };
		add.Click += (_, _) => _ = AddMusicAsync(outputSeconds);
		items.Add(add);

		if (index is { } i && i < _music.Count)
		{
			var edit = new MenuItem { Header = Strings.Music_Edit };
			edit.Click += (_, _) => ShowMusicEditor(i);
			var duplicate = new MenuItem { Header = Strings.Music_Duplicate };
			duplicate.Click += (_, _) => DuplicateMusic(i);
			var remove = new MenuItem { Header = Strings.Music_Remove };
			remove.Click += (_, _) => RemoveMusic(i);
			items.AddRange([edit, duplicate, remove]);
		}

		new ContextMenu { ItemsSource = items }.Open(MusicTracks);
	}

	private void RemoveMusic(int index)
	{
		if (index < 0 || index >= _music.Count) return;

		List<MusicClip> rest = [.. _music];
		rest.RemoveAt(index);
		SetMusicSorted(rest);
		CommitHistory("music");
		SealHistory();
	}

	/// <summary>Right after the original when there is room for it, else on top of it.</summary>
	private void DuplicateMusic(int index)
	{
		if (index < 0 || index >= _music.Count) return;

		MusicClip original = _music[index];
		double start = original.EndSeconds + original.LengthSeconds <= OutputSeconds + 1e-6 ? original.EndSeconds : original.StartSeconds;
		MusicClip copy = original with { StartSeconds = start };
		SetMusicSorted([.. _music, copy], copy);
		CommitHistory("music");
		SealHistory();
	}

	/// <summary>The clip's settings in a flyout on the track: every change is applied at once and is one step of the history while it is being typed.</summary>
	private void ShowMusicEditor(int index)
	{
		if (index < 0 || index >= _music.Count) return;

		MusicClip clip = _music[index];
		string key = "music-edit:" + index;
		var panel = new StackPanel { Spacing = 6, Width = 300 };
		bool building = true;

		void Apply(Func<MusicClip, MusicClip> change)
		{
			if (building || index >= _music.Count) return;

			List<MusicClip> clips = [.. _music];
			clips[index] = change(clips[index]);
			List<MusicClip> normalized = MusicTrack.Normalize(clips, Math.Max(OutputSeconds, 0), false);
			if (normalized.Count != clips.Count) return;

			_music = normalized;
			ShowMusic();
			CommitHistory(key);
		}

		NumericUpDown Field(string label, double value, double minimum, double maximum, double increment, Func<MusicClip, double, MusicClip> set)
		{
			var box = new NumericUpDown
			{
				Minimum = (decimal)minimum,
				Maximum = (decimal)maximum,
				Increment = (decimal)increment,
				FormatString = "0.0#",
				Value = (decimal)Math.Round(Math.Clamp(value, minimum, maximum), 3),
				HorizontalAlignment = HorizontalAlignment.Stretch
			};
			box.ValueChanged += (_, e) =>
			{
				if (e.NewValue is { } number) Apply(c => set(c, (double)number));
			};
			panel.Children.Add(new TextBlock { Text = label, Classes = { "infoLabel" }, Margin = new Avalonia.Thickness(0) });
			panel.Children.Add(box);
			return box;
		}

		double output = Math.Max(OutputSeconds, clip.LengthSeconds);
		Field(Strings.Music_Start, clip.StartSeconds, 0, output, 0.1, (c, v) => c with { StartSeconds = v });
		Field(Strings.Music_Length, clip.LengthSeconds, MusicTrack.MinLengthSeconds, output, 0.1, (c, v) => c with { LengthSeconds = v });
		if (clip.SourceSeconds > 0)
			Field(Strings.Music_In, clip.InSeconds, 0, clip.SourceSeconds, 0.1, (c, v) => c with { InSeconds = v });
		Field(Strings.Music_Level, clip.Db, MusicTrack.SilentDb, MusicTrack.MaxDb, 1, (c, v) => c with { Db = v });
		Field(Strings.Music_FadeIn, clip.FadeInSeconds, 0, output, 0.5, (c, v) => c with { FadeInSeconds = v });
		Field(Strings.Music_FadeOut, clip.FadeOutSeconds, 0, output, 0.5, (c, v) => c with { FadeOutSeconds = v });
		NumericUpDown duck = Field(Strings.Music_Duck, clip.DuckDb, MusicTrack.SilentDb, 0, 1, (c, v) => c with { DuckDb = v });
		ToolTip.SetTip(duck, Strings.Music_DuckTip);

		var loop = new CheckBox { Content = Strings.Music_Loop, IsChecked = clip.Loop, IsEnabled = clip.SourceSeconds > 0 };
		loop.IsCheckedChanged += (_, _) => Apply(c => c with { Loop = loop.IsChecked == true });
		panel.Children.Add(loop);
		building = false;

		var flyout = new Flyout { Content = panel };
		flyout.Closed += (_, _) =>
		{
			SetMusicSorted(_music);
			SealHistory();
		};
		flyout.ShowAt(MusicTracks, true);
	}
}
