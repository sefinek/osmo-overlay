using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Gui;

/// <summary>
///     Preview transport - go to start/end, frame step, play/pause - and every preview keyboard shortcut, NLE
///     style: Space play/pause, Left/Right one frame, Home/End, I/O mark the start/end of a part to cut out and X/Delete cuts it (MainWindow.Cuts.cs), M mutes (MainWindow.Audio.cs).
/// </summary>
public partial class MainWindow
{
	private void WirePreviewShortcuts()
	{
		// Tunnel: Space must reach play/pause before a focused button treats it as its own click.
		AddHandler(KeyDownEvent, OnPreviewSpaceKeyDown, RoutingStrategies.Tunnel);
		// Bubble: a focused list or number box keeps its own arrow/Home/End handling.
		KeyDown += OnPreviewShortcutKeyDown;
	}

	private bool PreviewShortcutsActive => TransportPanel.IsEnabled && FocusManager?.GetFocusedElement() is not TextBox;

	private void OnPreviewSpaceKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || !PreviewShortcutsActive) return;

		TogglePlayback();
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
			Key.X or Key.Delete => () => ApplyCutAction(CutAction.CutSelection),
			Key.M => ToggleMute,
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
