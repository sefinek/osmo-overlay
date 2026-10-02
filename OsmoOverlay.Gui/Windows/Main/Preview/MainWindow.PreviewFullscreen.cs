using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     The preview full screen (F11 or the toolbar button), on the monitor chosen in Settings (OverlaySettings.PreviewMonitor)
///     or else the main window's own. The viewport - video, overlay canvas and all - is moved into a PreviewFullscreenWindow
///     and back rather than mirrored, as a played frame goes to exactly one VideoView (PlaybackFrameSource). A VideoView
///     starts empty when it's attached, so playback pauses for the move and the frame is shown again after it. While
///     it's full screen the zoom is Fit and the editor is out of the way: no guides, no selection, no dragging.
///     Space, the arrows, J/K/L and the other preview shortcuts work there; a click plays or pauses; Esc/F11 leave.
/// </summary>
public partial class MainWindow
{
	private PreviewFullscreenWindow? _fullscreenWindow;
	private Panel? _viewportHost;
	private int _viewportIndex;
	private double? _zoomBeforeFullscreen;
	private Vector _panBeforeFullscreen;
	private bool _previewFullscreen;
	private bool _closing;

	private void WirePreviewFullscreen()
	{
		Closing += (_, e) => _closing = !e.Cancel;
	}

	private void OnToggleFullscreenClick(object? sender, RoutedEventArgs e)
	{
		if (_viewportOnSecondScreen)
		{
			PausePlayback();
			return;
		}

		TogglePreviewFullscreen();
	}

	private void TogglePreviewFullscreen()
	{
		if (_fullscreenWindow is { } open)
		{
			open.Close();
			return;
		}

		if (_summary is null || _previewFrameSize is null || PreviewViewport.Parent is not Panel host) return;

		_zoomBeforeFullscreen = _previewZoom;
		_panBeforeFullscreen = _previewPan;
		_previewFullscreen = true;
		FullscreenButton.Classes.Set("active", true);
		SetPreviewZoom(null, null);
		HideHoverIcons();
		OverlayDragCanvas.IsHitTestVisible = false;

		Screen? screen = PreviewMonitor();
		var window = new PreviewFullscreenWindow(DetachViewport(host), screen);
		PreviewFullscreenNoticeTitle.Text = "The preview is full screen";
		PreviewFullscreenNoticeText.Text = $"It's showing on {DescribeScreen(screen)}. Press Esc or F11 there, or the button below, to bring it back here.";
		PreviewFullscreenNotice.IsVisible = true;
		window.AddHandler(KeyDownEvent, OnPreviewSpaceKeyDown, RoutingStrategies.Tunnel);
		window.AddHandler(KeyUpEvent, OnPreviewSpaceKeyUp, RoutingStrategies.Tunnel);
		window.KeyDown += OnPreviewShortcutKeyDown;
		window.PointerPressed += (_, e) =>
		{
			if (e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) TogglePlayback();
		};
		window.Closed += (_, _) => LeavePreviewFullscreen(window);
		_fullscreenWindow = window;

		MovePreviewViewport(() => window.Show(this));
	}

	private Control DetachViewport(Panel host)
	{
		_viewportHost = host;
		_viewportIndex = host.Children.IndexOf(PreviewViewport);
		host.Children.RemoveAt(_viewportIndex);
		PreviewViewport.CornerRadius = default;
		return PreviewViewport;
	}

	private void LeavePreviewFullscreen(PreviewFullscreenWindow window)
	{
		_fullscreenWindow = null;
		if (_closing) return;

		window.Content = null;
		MovePreviewViewport(() =>
		{
			PreviewViewport.CornerRadius = new CornerRadius(6);
			_viewportHost!.Children.Insert(_viewportIndex, PreviewViewport);
		});

		_previewFullscreen = false;
		PreviewFullscreenNotice.IsVisible = false;
		FullscreenButton.Classes.Set("active", false);
		OverlayDragCanvas.IsHitTestVisible = true;
		SetPreviewZoom(_zoomBeforeFullscreen, null);
		_previewPan = _panBeforeFullscreen;
		ApplyPreviewLayout();
	}

	/// <summary>
	///     Runs `move` (which attaches the VideoView somewhere else) with playback paused, then shows the frame again once
	///     the view has its compositor visual - from a still, or by playing on from where it was.
	/// </summary>
	private void MovePreviewViewport(Action move)
	{
		bool wasPlaying = _previewPlayer.IsPlaying;
		PausePlayback();
		move();

		Dispatcher.UIThread.Post(() =>
		{
			if (_closing) return;

			TimeSpan position = _previewPosition;
			if (wasPlaying)
			{
				_previewPlayer.Play(position);
				ShowPlayingState(true);
			}
			else
			{
				_previewPlayer.RequestSeek(position);
			}
		}, DispatcherPriority.Loaded);
	}

	/// <summary>The screen as Settings lists it ("Display 2 - 2560x1440"), with how it was picked.</summary>
	private string DescribeScreen(Screen? screen)
	{
		if (screen is null) return "this window's screen";

		int index = Screens.All.ToList().FindIndex(s => MonitorChoice.KeyOf(s) == MonitorChoice.KeyOf(screen));
		string name = MonitorChoice.Describe(screen, Math.Max(index, 0));
		return OverlaySettingsStore.Load().PreviewMonitor is null ? $"{name} (the same screen as this window)" : name;
	}

	/// <summary>The screen Settings chose for the full screen preview, or the main window's own when none is chosen or it's gone.</summary>
	private Screen? PreviewMonitor()
	{
		return MonitorChoice.Find(Screens.All, OverlaySettingsStore.Load().PreviewMonitor)
		       ?? Screens.ScreenFromWindow(this) ?? Screens.Primary;
	}
}
