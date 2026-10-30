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
	private double? _zoomBeforeViewportMove;
	private Vector _panBeforeViewportMove;
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

		Screen? screen = PreviewMonitor();
		_previewFullscreen = true;
		FullscreenButton.Classes.Set("active", true);
		BeginViewportMove(Strings.Main_ThePreviewIsFullScreen, string.Format(Strings.Fullscreen_NoticeText, DescribeScreen(screen)));
		var window = new PreviewFullscreenWindow(DetachViewport(host), screen);
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

	/// <summary>
	///     The editor steps aside before the viewport leaves the main window (full screen or second screen): zoom Fit, no
	///     hover (shown at once - a fade cut off by the move would stay on the picture), no hit-testing, and the notice in
	///     the place the viewport was.
	/// </summary>
	private void BeginViewportMove(string noticeTitle, string noticeText)
	{
		_zoomBeforeViewportMove = _previewZoom;
		_panBeforeViewportMove = _previewPan;
		SetPreviewZoom(null, null);
		HideHoverIcons(true);
		OverlayDragCanvas.IsHitTestVisible = false;
		PreviewFullscreenNoticeTitle.Text = noticeTitle;
		PreviewFullscreenNoticeText.Text = noticeText;
		PreviewFullscreenNotice.IsVisible = true;
	}

	/// <summary>The viewport is back in the main window: the zoom and pan from before the move, the editor active again.</summary>
	private void EndViewportMove()
	{
		PreviewFullscreenNotice.IsVisible = false;
		OverlayDragCanvas.IsHitTestVisible = true;
		SetPreviewZoom(_zoomBeforeViewportMove, null);
		_previewPan = _panBeforeViewportMove;
		ApplyPreviewLayout();
	}

	private Control DetachViewport(Panel host)
	{
		_viewportHost = host;
		_viewportIndex = host.Children.IndexOf(PreviewViewport);
		host.Children.RemoveAt(_viewportIndex);
		PreviewViewport.CornerRadius = default;
		return PreviewViewport;
	}

	private void ReattachViewport()
	{
		PreviewViewport.CornerRadius = new CornerRadius(6);
		_viewportHost!.Children.Insert(_viewportIndex, PreviewViewport);
	}

	private void LeavePreviewFullscreen(PreviewFullscreenWindow window)
	{
		_fullscreenWindow = null;
		if (_closing) return;

		window.Content = null;
		MovePreviewViewport(ReattachViewport);

		_previewFullscreen = false;
		FullscreenButton.Classes.Set("active", false);
		EndViewportMove();
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

			TimeSpan position = TimeSpan.FromSeconds(PreviewTimeline.Value);
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
		if (screen is null) return Strings.Fullscreen_ThisWindowsScreen;

		int index = Screens.All.ToList().FindIndex(s => MonitorChoice.KeyOf(s) == MonitorChoice.KeyOf(screen));
		string name = MonitorChoice.Describe(screen, Math.Max(index, 0));
		return OverlaySettingsStore.Load().PreviewMonitor is null ? string.Format(Strings.Fullscreen_SameScreen, name) : name;
	}

	/// <summary>The screen Settings chose for the full screen preview, or the main window's own when none is chosen or it's gone.</summary>
	private Screen? PreviewMonitor()
	{
		return MonitorChoice.Find(Screens.All, OverlaySettingsStore.Load().PreviewMonitor)
		       ?? Screens.ScreenFromWindow(this) ?? Screens.Primary;
	}
}
