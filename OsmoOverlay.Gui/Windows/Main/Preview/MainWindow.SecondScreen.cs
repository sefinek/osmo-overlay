using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     The second screen (OverlaySettings.SecondScreenEnabled/SecondScreenMonitor, F9): a SecondScreenWindow on another
///     monitor showing the log - the render's progress too - and, while the preview plays, the preview's viewport, moved
///     there and back like the full screen's (MainWindow.PreviewFullscreen.cs), so playback pauses for a moment at each
///     move. Pausing or the end of the recording brings the viewport home and the log back. Not combined with the full screen
///     preview: while that's open the viewport stays where it is. Space, the arrows, J/K/L and the other preview shortcuts
///     work in the window, a click plays or pauses; Esc/F9 close it.
/// </summary>
public partial class MainWindow
{
	private SecondScreenWindow? _secondScreen;
	private bool _viewportOnSecondScreen;
	// Set while a move pauses and restarts the playback, so its PlaybackStopped/PlaybackStarted don't start another move.
	private bool _movingViewport;
	private string? _secondScreenKey;
	private string _secondScreenName = "";

	private void WireSecondScreen()
	{
		KeyDown += (_, e) =>
		{
			if (e.Key != Key.F9 || e.KeyModifiers != KeyModifiers.None || FocusManager?.GetFocusedElement() is TextBox) return;

			ToggleSecondScreen();
			e.Handled = true;
		};
	}

	private void ToggleSecondScreen()
	{
		bool enable = _secondScreen is null;
		OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { SecondScreenEnabled = enable });
		if (enable) OpenSecondScreen();
		else CloseSecondScreen();
	}

	/// <summary>After Settings: opens, closes or moves the window to match what's chosen there.</summary>
	private void ApplySecondScreenSettings(OverlaySettings settings)
	{
		bool moved = _secondScreen is not null && settings.SecondScreenEnabled && SecondScreenTarget(settings) is { } target
		             && MonitorChoice.KeyOf(target) != _secondScreenKey;
		if (_secondScreen is not null && (!settings.SecondScreenEnabled || moved)) CloseSecondScreen();
		if (_secondScreen is null && settings.SecondScreenEnabled) OpenSecondScreen();
	}

	private void OpenSecondScreen()
	{
		if (_secondScreen is not null) return;

		if (SecondScreenTarget(OverlaySettingsStore.Load()) is not { } screen)
		{
			AppendLog(Strings.SecondScreen_NoMonitor, LogLevel.Warn);
			return;
		}

		var window = new SecondScreenWindow(screen);
		window.CopyLogFrom(LogBox.Inlines);
		window.AddHandler(KeyDownEvent, OnPreviewSpaceKeyDown, RoutingStrategies.Tunnel);
		window.AddHandler(KeyUpEvent, OnPreviewSpaceKeyUp, RoutingStrategies.Tunnel);
		window.KeyDown += OnPreviewShortcutKeyDown;
		window.PointerPressed += (_, e) =>
		{
			if (_viewportOnSecondScreen && e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) TogglePlayback();
		};
		window.CloseRequested += ToggleSecondScreen;
		window.Closed += (_, _) => OnSecondScreenClosed(window);

		_secondScreen = window;
		_secondScreenKey = MonitorChoice.KeyOf(screen);
		_secondScreenName = DescribeSecondScreen(screen);
		window.ShowLog();
		window.Show(this);
		AppendLog(string.Format(Strings.SecondScreen_On, _secondScreenName));
		if (_previewPlayer.IsPlaying) MoveViewportToSecondScreen();
	}

	private void CloseSecondScreen()
	{
		if (_secondScreen is not { } window) return;

		if (_viewportOnSecondScreen) MoveViewportHome(window);
		_secondScreen = null;
		window.Close();
		AppendLog(Strings.SecondScreen_Closed);
	}

	/// <summary>The window went away on its own (Alt+F4, the screen was unplugged) - it stays off until it's turned on again.</summary>
	private void OnSecondScreenClosed(SecondScreenWindow window)
	{
		if (_secondScreen != window) return;

		_secondScreen = null;
		if (_closing) return;

		if (_viewportOnSecondScreen) MoveViewportHome(window);
		OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { SecondScreenEnabled = false });
		AppendLog(Strings.SecondScreen_Closed);
	}

	/// <summary>The screen chosen in Settings, or the first one the main window isn't on; null when there's no other monitor.</summary>
	private Screen? SecondScreenTarget(OverlaySettings settings)
	{
		if (MonitorChoice.Find(Screens.All, settings.SecondScreenMonitor) is { } chosen) return chosen;

		string? mainKey = Screens.ScreenFromWindow(this) is { } main ? MonitorChoice.KeyOf(main) : null;
		return Screens.All.FirstOrDefault(s => MonitorChoice.KeyOf(s) != mainKey);
	}

	private string DescribeSecondScreen(Screen screen)
	{
		int index = Screens.All.ToList().FindIndex(s => MonitorChoice.KeyOf(s) == MonitorChoice.KeyOf(screen));
		return MonitorChoice.Describe(screen, Math.Max(index, 0));
	}

	private void OnSecondScreenPlaybackStarted()
	{
		if (_previewBenchmarkRunning || _secondScreen is null || _movingViewport || _viewportOnSecondScreen || _previewFullscreen) return;

		// Not from inside the event: the move pauses and restarts the playback that's just starting.
		Dispatcher.UIThread.Post(() =>
		{
			if (_previewPlayer.IsPlaying) MoveViewportToSecondScreen();
		});
	}

	private void OnSecondScreenPlaybackStopped()
	{
		if (_previewBenchmarkRunning || _secondScreen is not { } window || _movingViewport || !_viewportOnSecondScreen) return;

		Dispatcher.UIThread.Post(() =>
		{
			if (!_previewPlayer.IsPlaying && _viewportOnSecondScreen) MoveViewportHome(window);
		});
	}

	private void MoveViewportToSecondScreen()
	{
		if (_secondScreen is not { } window || _viewportOnSecondScreen || _previewFullscreen || PreviewViewport.Parent is not Panel host) return;

		_zoomBeforeFullscreen = _previewZoom;
		_panBeforeFullscreen = _previewPan;
		SetPreviewZoom(null, null);
		HideHoverIcons();
		OverlayDragCanvas.IsHitTestVisible = false;
		PreviewFullscreenNoticeTitle.Text = Strings.SecondScreen_NoticeTitle;
		PreviewFullscreenNoticeText.Text = string.Format(Strings.SecondScreen_NoticeText, _secondScreenName);
		PreviewFullscreenNotice.IsVisible = true;

		MoveViewport(() =>
		{
			_viewportHost = host;
			_viewportIndex = host.Children.IndexOf(PreviewViewport);
			host.Children.RemoveAt(_viewportIndex);
			// Settled while it's in no window: moved with a layout pass still queued for it here, the main window's
			// pass throws "InvalidateArrange on wrong LayoutManager" and the viewport is never arranged.
			UpdateLayout();
			PreviewViewport.CornerRadius = default;
			window.ShowViewport(PreviewViewport);
			_viewportOnSecondScreen = true;
		});
	}

	private void MoveViewportHome(SecondScreenWindow window)
	{
		if (!_viewportOnSecondScreen) return;

		_viewportOnSecondScreen = false;
		MoveViewport(() =>
		{
			window.TakeViewport();
			window.UpdateLayout();
			window.ShowLog();
			PreviewViewport.CornerRadius = new CornerRadius(6);
			_viewportHost!.Children.Insert(_viewportIndex, PreviewViewport);
		});

		PreviewFullscreenNotice.IsVisible = false;
		OverlayDragCanvas.IsHitTestVisible = true;
		SetPreviewZoom(_zoomBeforeFullscreen, null);
		_previewPan = _panBeforeFullscreen;
		ApplyPreviewLayout();
	}

	private void MoveViewport(Action move)
	{
		_movingViewport = true;
		MovePreviewViewport(move);
		Dispatcher.UIThread.Post(() => _movingViewport = false, DispatcherPriority.Background);
	}
}
