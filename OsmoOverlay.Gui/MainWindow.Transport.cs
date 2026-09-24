using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Gui;

/// <summary>
///     Preview transport - go to start/end, frame step, play/pause - and every preview keyboard shortcut, NLE
///     style: Space play/pause, Left/Right one frame, Home/End, I/O mark the start/end of a part to cut out and X/Delete cuts it (MainWindow.Cuts.cs), M mutes (MainWindow.Audio.cs), H hides the overlay (MainWindow.PreviewTools.cs).
/// </summary>
public partial class MainWindow
{
	private void WirePreviewShortcuts()
	{
		// Tunnel: Space must reach play/pause before a focused button treats it as its own click - on the key's
		// release too, which is when a button clicks.
		AddHandler(KeyDownEvent, OnPreviewSpaceKeyDown, RoutingStrategies.Tunnel);
		AddHandler(KeyUpEvent, OnPreviewSpaceKeyUp, RoutingStrategies.Tunnel);
		// Bubble: a focused list or number box keeps its own arrow/Home/End handling.
		KeyDown += OnPreviewShortcutKeyDown;
	}

	private bool _spaceTookPlayback;

	private bool PreviewShortcutsActive => TransportPanel.IsEnabled && FocusManager?.GetFocusedElement() is not TextBox;

	private void OnPreviewSpaceKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || !PreviewShortcutsActive) return;

		TogglePlayback();
		_spaceTookPlayback = true;
		e.Handled = true;
	}

	private void OnPreviewSpaceKeyUp(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Space || !_spaceTookPlayback) return;

		_spaceTookPlayback = false;
		e.Handled = true;
	}

	private void OnPreviewShortcutKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.KeyModifiers != KeyModifiers.None || !PreviewShortcutsActive) return;

		Action? action = e.Key switch
		{
			Key.Left => () => StepFrames(-1),
			Key.Right => () => StepFrames(1),
			Key.Home => GoToStart,
			Key.End => GoToEnd,
			Key.I => () => ApplyCutAction(CutAction.MarkIn),
			Key.O => () => ApplyCutAction(CutAction.MarkOut),
			Key.X => () => ApplyCutAction(CutAction.CutSelection),
			Key.Delete => DeleteSelectedCutOrCutSelection,
			Key.Up => () => JumpToMarker(-1),
			Key.Down => () => JumpToMarker(1),
			Key.Q => ToggleLoop,
			Key.J => () => StepSpeed(-1),
			Key.K => PausePlayback,
			Key.L => () => StepSpeed(1),
			Key.M => ToggleMute,
			Key.H => ToggleOverlay,
			_ => null
		};
		if (action is null) return;

		action();
		e.Handled = true;
	}

	private void OnPlayPauseClick(object? sender, RoutedEventArgs e)
	{
		TogglePlayback();
	}

	private void OnGoToStartClick(object? sender, RoutedEventArgs e)
	{
		GoToStart();
	}

	private void OnPreviousFrameClick(object? sender, RoutedEventArgs e)
	{
		StepFrames(-1);
	}

	private void OnNextFrameClick(object? sender, RoutedEventArgs e)
	{
		StepFrames(1);
	}

	private void OnGoToEndClick(object? sender, RoutedEventArgs e)
	{
		GoToEnd();
	}

	private static readonly double[] PlaybackRates = [0.25, 0.5, 1, 2, 4];
	private bool _loopEnabled;

	private void OnLoopClick(object? sender, RoutedEventArgs e)
	{
		ToggleLoop();
	}

	private void ToggleLoop()
	{
		_loopEnabled = !_loopEnabled;
		LoopButton.Classes.Set("active", _loopEnabled);
		ToolTip.SetTip(LoopButton, _loopEnabled
			? "Looping (Q to stop) - the In/Out selection, or the whole recording without one"
			: "Loop playback (Q) - the In/Out selection, or the whole recording without one");
		SyncLoop();
	}

	/// <summary>The player loops the current In/Out selection - called again whenever the selection changes.</summary>
	private void SyncLoop()
	{
		TimeRange? range = _summary is not null && Selection is { } selection ? selection.ToTimeRange(_summary.Video.Fps) : null;
		_previewPlayer.SetLoop(_loopEnabled, range);
	}

	/// <summary>Up/Down: to the previous/next edge of a cut, the selection or a GPS loss, or the start/end (TimelineMarkers).</summary>
	private void JumpToMarker(int direction)
	{
		if (_summary is null) return;

		var fps = _summary.Video.Fps;
		IEnumerable<FrameRange> gpsLoss = PreviewTimeline.GpsLoss.Select(r =>
			new FrameRange((long)Math.Round(r.StartSeconds * fps), (long)Math.Round(r.EndSeconds * fps)));
		SortedSet<long> markers = TimelineMarkers.Collect(SourceFrames, CutList.Normalize(_cuts, SourceFrames), Selection, gpsLoss);
		var current = CurrentFrame();
		if ((direction > 0 ? TimelineMarkers.Next(markers, current) : TimelineMarkers.Previous(markers, current)) is { } target)
			SeekToFrame(target);
	}
	private bool _suppressSpeedEvent;

	private void WireSpeed()
	{
		_suppressSpeedEvent = true;
		SpeedCombo.ItemsSource = PlaybackRates.Select(FormatRate).ToList();
		SpeedCombo.SelectedIndex = Array.IndexOf(PlaybackRates, 1.0);
		_suppressSpeedEvent = false;
	}

	private static string FormatRate(double rate)
	{
		return $"{rate.ToString("0.##", CultureInfo.InvariantCulture)}x";
	}

	private void OnSpeedChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressSpeedEvent || SpeedCombo.SelectedIndex < 0) return;

		_previewPlayer.SetPlaybackRate(PlaybackRates[SpeedCombo.SelectedIndex]);
	}

	/// <summary>
	///     NLE-style J/L: L starts playing at 1x, or speeds up if already playing; J slows down. No reverse play - decoding
	///     backwards means re-seeking for every frame, which a 4K HEVC stream can't do at playback speed.
	/// </summary>
	private void StepSpeed(int direction)
	{
		var current = Array.IndexOf(PlaybackRates, _previewPlayer.PlaybackRate);
		if (direction > 0 && !_previewPlayer.IsPlaying)
		{
			SetSpeed(Array.IndexOf(PlaybackRates, 1.0));
			TogglePlayback();
			return;
		}

		SetSpeed(Math.Clamp(current + direction, 0, PlaybackRates.Length - 1));
	}

	private void SetSpeed(int index)
	{
		_suppressSpeedEvent = true;
		SpeedCombo.SelectedIndex = index;
		_suppressSpeedEvent = false;
		_previewPlayer.SetPlaybackRate(PlaybackRates[index]);
	}

	private void PausePlayback()
	{
		if (!_previewPlayer.IsPlaying) return;

		_previewPlayer.Pause();
		ShowPlayingState(false);
	}

	private void TogglePlayback()
	{
		var wasPlaying = _previewPlayer.IsPlaying;
		_previewPlayer.TogglePlayPause(TimeSpan.FromSeconds(PreviewTimeline.Value));
		if (!wasPlaying) ShowPlayingState(true);
	}

	/// <summary>The one place the play/pause button's icon and tooltip follow the playback state.</summary>
	private void ShowPlayingState(bool playing)
	{
		PlayPauseIcon.Data = playing ? Icons.Pause : Icons.Play;
		ToolTip.SetTip(PlayPauseButton, playing ? "Pause (Space)" : "Play (Space)");
	}

	private void GoToStart()
	{
		SeekToFrame(0);
	}

	private void GoToEnd()
	{
		// VideoFrameSource clamps past-the-end seeks to the last decodable frame.
		SeekToFrame(long.MaxValue);
	}

	private void StepFrames(int delta)
	{
		if (_summary is null) return;

		SeekToFrame(Math.Max(0, CurrentFrame() + delta));
	}

	/// <summary>The frame on screen - the same rule the decoders use (PreviewFrames.IndexAt), within the recording.</summary>
	private long CurrentFrame()
	{
		if (_summary is null) return 0;

		return Math.Min(PreviewFrames.IndexAt(PreviewTimeline.Value, _summary.Video.Fps), Math.Max(0, SourceFrames - 1));
	}

	/// <summary>
	///     Seeks a quarter frame before the frame's own start time: ffmpeg's -ss (millisecond precision) lands
	///     on the first frame at or after the position, so aiming exactly at a frame's timestamp could round
	///     past it and skip a frame on every step.
	/// </summary>
	private void SeekToFrame(long frame)
	{
		if (_summary is null) return;

		if (_previewPlayer.IsPlaying)
		{
			_previewPlayer.Pause();
			ShowPlayingState(false);
		}

		var seconds = frame == long.MaxValue ? PreviewTimeline.Maximum : (frame - 0.25) / _summary.Video.Fps;
		PreviewTimeline.Value = Math.Clamp(seconds, 0, PreviewTimeline.Maximum);
	}
}
