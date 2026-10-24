using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Preview;
using OsmoOverlay.Core.Telemetry;
using SkiaSharp;

namespace OsmoOverlay.Gui;

/// <summary>
///     Exported videos played side by side on one clock (ComparePlayer) - to see what a release of the program changed in
///     the same frames of the same recording. The window only shows and steers; the tiles' decoding and timing are the
///     player's. Changing the set of videos opens every one again.
/// </summary>
public partial class ComparePlaybackWindow : Window
{
	private const int MaxFiles = 6;

	// The widest a picture is decoded: Full HD is what the zoom has to look into (measured on three tiles, 960 to 1920 px cost the
	// same - the decoder is the limit), and a 4K picture would only cost four times the memory for a view that is rarely that large.
	private const int TileDecodeWidth = 1920;

	private static readonly double[] Rates = [0.25, 0.5, 1, 2];

	private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);

	private const double WheelZoomStep = 1.2;

	// A notch of the wheel moves the view by this fraction of what is in view.
	private const double WheelPanStep = 0.1;
	private const double KeyZoomStep = 1.25;

	private readonly List<string> _paths = [];
	private readonly List<TileZoomView> _zoomViews = [];
	private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
	private ComparePlayer? _player;
	private int _generation;
	private bool _suppressTimeline;
	private bool _resumeAfterScrub;
	private bool _loop;
	private double _rate = 1;
	private (TimeSpan Position, SeekAccuracy Accuracy)? _pendingSeek;
	private bool _seekRunning;
	private CompareZoom _zoom = CompareZoom.Whole;
	private (TileZoomView View, Point At)? _pan;

	public ComparePlaybackWindow()
	{
		InitializeComponent();
		SpeedCombo.ItemsSource = Rates.Select(r => r.ToString("0.##", CultureInfo.InvariantCulture) + "x").ToList();
		SpeedCombo.SelectedIndex = Array.IndexOf(Rates, 1.0);
		Timeline.ScrubStarted += OnScrubStarted;
		Timeline.ScrubEnded += OnScrubEnded;
		_timer.Tick += (_, _) => UpdatePosition();
		KeyDown += OnWindowKeyDown;
		ApplyZoom();
		Closed += (_, _) =>
		{
			_generation++;
			_timer.Stop();
			DisposePlayer();
		};
	}

	private async void OnAddClick(object? sender, RoutedEventArgs e)
	{
		if (_paths.Count >= MaxFiles)
		{
			await ShowLimitAsync();
			return;
		}

		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenRememberedAsync("compare-videos", new FilePickerOpenOptions
		{
			Title = Strings.Compare_PickVideos,
			AllowMultiple = true,
			FileTypeFilter = [new FilePickerFileType(Strings.Common_Mp4Video) { Patterns = ["*.mp4", "*.MP4"] }]
		});

		bool added = false;
		bool skippedForLimit = false;
		foreach (IStorageFile file in files)
		{
			string path = file.Path.LocalPath;
			if (_paths.Contains(path)) continue;

			if (_paths.Count >= MaxFiles)
			{
				skippedForLimit = true;
				break;
			}

			_paths.Add(path);
			added = true;
		}

		if (added) await RunAsync(ReloadAsync);
		if (skippedForLimit) await ShowLimitAsync();
	}

	private Task ShowLimitAsync()
	{
		return ConfirmDialog.ShowAsync(this, Strings.Compare_LimitTitle, string.Format(Strings.Compare_LimitMessage, MaxFiles),
			kind: DialogKind.Info, windowTitle: Strings.ComparePlay_Title);
	}

	private async void OnClearClick(object? sender, RoutedEventArgs e)
	{
		_paths.Clear();
		await RunAsync(ReloadAsync);
	}

	private async void OnRemoveClick(object? sender, RoutedEventArgs e)
	{
		if (sender is not Button { Tag: string path }) return;

		_paths.Remove(path);
		await RunAsync(ReloadAsync);
	}

	private async Task ReloadAsync()
	{
		int generation = ++_generation;
		TimeSpan resumeAt = _player?.Position ?? TimeSpan.Zero;
		_timer.Stop();
		DisposePlayer();
		TilesGrid.Children.Clear();
		_zoomViews.Clear();
		_pan = null;
		TilesGrid.RowDefinitions.Clear();
		TilesGrid.ColumnDefinitions.Clear();
		TransportPanel.IsEnabled = false;
		Timeline.IsEnabled = false;
		SetPlayingIcon(false);
		UpdateTimeText(TimeSpan.Zero, TimeSpan.Zero);
		EmptyText.IsVisible = _paths.Count == 0;
		OpeningText.IsVisible = _paths.Count > 0;
		if (_paths.Count == 0) return;

		(ComparePlayer? player, IReadOnlyList<CompareFailure> failures) = await ComparePlayer.OpenAsync([.. _paths], TileDecodeWidth);
		if (generation != _generation)
		{
			player?.Dispose();
			return;
		}

		OpeningText.IsVisible = false;
		if (failures.Count > 0)
		{
			foreach (CompareFailure failure in failures) _paths.Remove(failure.Path);
			await ConfirmDialog.ShowAsync(this, Strings.ComparePlay_OpenFailedTitle,
				string.Format(Strings.ComparePlay_OpenFailed, string.Join("\n", failures.Select(f => $"{Path.GetFileName(f.Path)}: {f.Reason}"))),
				kind: DialogKind.Warning, windowTitle: Strings.ComparePlay_Title);
			if (generation != _generation)
			{
				player?.Dispose();
				return;
			}
		}

		if (player is null)
		{
			EmptyText.IsVisible = true;
			return;
		}

		_player = player;
		player.Loop = _loop;
		player.Rate = _rate;
		VideoView[] views = BuildTiles(player);
		// The frame is lent for the call, from a decoder's thread - copied once here, shown on the UI thread, which a view needs.
		player.StillReady += (index, frame) =>
		{
			SKImage still = VideoView.CopyStill(frame);
			Dispatcher.UIThread.Post(() =>
			{
				if (_player == player) views[index].ShowStill(still);
				else still.Dispose();
			});
		};
		player.PlaybackStarted += () => Dispatcher.UIThread.Post(() =>
		{
			if (_player != player) return;

			for (int i = 0; i < views.Length; i++) views[i].StartPlayback(player.Tiles[i].Frames);
		});
		player.Ended += () => Dispatcher.UIThread.Post(() =>
		{
			if (_player != player) return;

			SetPlayingIcon(false);
			_timer.Stop();
			UpdatePosition();
		});

		Timeline.Maximum = Math.Max(player.Duration.TotalSeconds, 0.001);
		Timeline.SetSources(null, null, player.Tiles[0].Fps, 16.0 / 9);
		Timeline.IsEnabled = true;
		TransportPanel.IsEnabled = true;
		await player.SeekAsync(resumeAt, SeekAccuracy.Exact);
		UpdatePosition();
	}

	private VideoView[] BuildTiles(ComparePlayer player)
	{
		int count = player.Tiles.Count;
		for (int c = 0; c < CompareLayout.Columns(count); c++) TilesGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
		for (int r = 0; r < CompareLayout.Rows(count); r++) TilesGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

		ExportMarkerInfo[] markers = [.. player.Tiles.Select(t => t.Marker).OfType<ExportMarkerInfo>()];
		bool[] differs =
		[
			markers.Select(m => m.Version).Distinct().Count() > 1,
			markers.Select(m => m.TelemetryVersion).Distinct().Count() > 1,
			markers.Select(m => m.Orientation).Distinct().Count() > 1
		];
		var views = new VideoView[count];
		for (int i = 0; i < count; i++)
		{
			CompareTile tile = player.Tiles[i];
			var view = new VideoView { Width = tile.Width, Height = tile.Height };
			views[i] = view;

			Border tileControl = BuildTile(tile, view, differs, player, i);
			Grid.SetColumn(tileControl, i % CompareLayout.Columns(count));
			Grid.SetRow(tileControl, i / CompareLayout.Columns(count));
			TilesGrid.Children.Add(tileControl);
		}

		return views;
	}

	private Border BuildTile(CompareTile tile, VideoView view, bool[] differs, ComparePlayer player, int index)
	{
		var remove = new Button
		{
			Tag = tile.Path,
			Padding = new Thickness(7),
			Content = new IconView { Data = Icons.Close, Width = 9, Height = 9 }
		};
		remove.Click += OnRemoveClick;
		ToolTip.SetTip(remove, Strings.ComparePlay_Remove);

		var name = new TextBlock
		{
			Text = Path.GetFileName(tile.Path),
			FontSize = 13,
			FontWeight = FontWeight.SemiBold,
			TextTrimming = TextTrimming.CharacterEllipsis,
			VerticalAlignment = VerticalAlignment.Center
		};
		ToolTip.SetTip(name, tile.Path);
		var header = new DockPanel { Margin = new Thickness(10, 4, 4, 0) };
		DockPanel.SetDock(remove, Dock.Right);
		header.Children.Add(remove);
		header.Children.Add(name);

		var marker = new TextBlock { FontSize = 12, Foreground = Palette.TextPrimary, TextWrapping = TextWrapping.Wrap };
		AddMarkerText(marker, tile.Marker, differs);
		var info = new TextBlock
		{
			Text = string.Format(Strings.ComparePlay_Info, tile.Source.Video.Width, tile.Source.Video.Height,
				tile.Fps.ToString("0.###", CultureInfo.InvariantCulture)),
			FontSize = 12,
			Foreground = Palette.TextMuted
		};
		var size = new TextBlock
		{
			Text = string.Format(Strings.ComparePlay_Size, tile.FileSizeBytes.ToString("N0", CultureInfo.CurrentCulture),
				FormatHelper.FormatBytes(tile.FileSizeBytes)),
			FontSize = 12,
			Foreground = Palette.TextMuted
		};
		var error = new TextBlock { FontSize = 12, Foreground = Palette.Danger, TextWrapping = TextWrapping.Wrap, IsVisible = false };
		player.TileFailed += failed =>
		{
			if (failed != index) return;

			Dispatcher.UIThread.Post(() =>
			{
				error.Text = string.Format(Strings.ComparePlay_TileError, tile.Error);
				error.IsVisible = true;
			});
		};

		var footer = new StackPanel { Margin = new Thickness(10, 6, 10, 8), Spacing = 2, Children = { marker, info, size, error } };
		var scale = new ScaleTransform();
		var shift = new TranslateTransform();
		var zoomed = new Viewbox { Stretch = Stretch.Uniform, Child = view, RenderTransform = new TransformGroup { Children = { scale, shift } } };
		var picture = new Panel { Background = Brushes.Transparent, ClipToBounds = true, Margin = new Thickness(4, 4, 4, 0), Children = { zoomed } };
		var zoomView = new TileZoomView(picture, scale, shift, tile.Width / (double)tile.Height);
		_zoomViews.Add(zoomView);
		picture.SizeChanged += (_, _) => ApplyZoom();
		picture.PointerWheelChanged += (_, e) => OnPictureWheel(zoomView, e);
		picture.PointerPressed += (_, e) => OnPicturePressed(zoomView, e);
		picture.PointerMoved += (_, e) => OnPictureMoved(zoomView, e);
		picture.PointerReleased += (_, e) => OnPictureReleased(zoomView, e);
		picture.DoubleTapped += (_, _) => SetZoom(CompareZoom.Whole);
		var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
		Grid.SetRow(header, 0);
		Grid.SetRow(picture, 1);
		Grid.SetRow(footer, 2);
		grid.Children.Add(header);
		grid.Children.Add(picture);
		grid.Children.Add(footer);

		return new Border
		{
			Child = grid,
			Background = Palette.SurfaceSunken,
			BorderBrush = Palette.Stroke,
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(8),
			ClipToBounds = true
		};
	}

	[GeneratedRegex(@"(\{\d\})")]
	private static partial Regex MarkerPlaceholder();

	/// <summary>
	///     What the file says about the program that rendered it. A part that differs between the videos is picked out in the
	///     accent color - a difference is what the comparison is for, not a fault, so no status color.
	/// </summary>
	private static void AddMarkerText(TextBlock block, ExportMarkerInfo? marker, bool[] differs)
	{
		if (marker is null)
		{
			block.Inlines!.Add(new Run(Strings.ComparePlay_NoMarker) { Foreground = Palette.TextMuted });
			return;
		}

		string[] values =
		[
			marker.Version,
			marker.TelemetryVersion.ToString(CultureInfo.InvariantCulture),
			marker.Orientation == OrientationSource.Quaternion ? Strings.ComparePlay_TiltOrientation : Strings.ComparePlay_TiltAccelerometer
		];
		foreach (string part in MarkerPlaceholder().Split(Strings.ComparePlay_Marker))
		{
			if (part.Length == 3 && part[0] == '{' && part[2] == '}' && char.IsAsciiDigit(part[1]) && part[1] - '0' is var index && index < values.Length)
			{
				block.Inlines!.Add(differs[index]
					? new Run(values[index]) { Foreground = Palette.Accent, FontWeight = FontWeight.SemiBold }
					: new Run(values[index]));
			}
			else if (part.Length > 0)
			{
				block.Inlines!.Add(new Run(part));
			}
		}

		if (differs.Any(d => d)) ToolTip.SetTip(block, Strings.ComparePlay_Differs);
	}

	private void DisposePlayer()
	{
		ComparePlayer? player = _player;
		_player = null;
		player?.Dispose();
	}

	private async void OnPlayPauseClick(object? sender, RoutedEventArgs e)
	{
		await RunAsync(TogglePlayAsync);
	}

	private async Task TogglePlayAsync()
	{
		if (_player is not { } player) return;

		if (player.IsPlaying)
		{
			await player.PauseAsync();
			SetPlayingIcon(false);
			_timer.Stop();
			UpdatePosition();
			return;
		}

		await player.PlayAsync();
		SetPlayingIcon(true);
		_timer.Start();
	}

	private async void OnGoToStartClick(object? sender, RoutedEventArgs e)
	{
		await GoToAsync(false);
	}

	private async void OnGoToEndClick(object? sender, RoutedEventArgs e)
	{
		await GoToAsync(true);
	}

	private async Task GoToAsync(bool end)
	{
		if (_player is not { } player) return;

		await RunAsync(() => player.SeekAsync(end ? player.Duration : TimeSpan.Zero, SeekAccuracy.Exact));
		UpdatePosition();
	}

	private async void OnPreviousFrameClick(object? sender, RoutedEventArgs e)
	{
		await StepAsync(-1);
	}

	private async void OnNextFrameClick(object? sender, RoutedEventArgs e)
	{
		await StepAsync(1);
	}

	private async Task StepAsync(int frames)
	{
		if (_player is not { } player) return;

		await RunAsync(() => player.StepAsync(frames));
		SetPlayingIcon(false);
		_timer.Stop();
		UpdatePosition();
	}

	private void OnLoopClick(object? sender, RoutedEventArgs e)
	{
		_loop = !_loop;
		LoopButton.Classes.Set("active", _loop);
		if (_player is { } player) player.Loop = _loop;
	}

	private void OnSpeedChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (SpeedCombo.SelectedIndex < 0) return;

		_rate = Rates[SpeedCombo.SelectedIndex];
		if (_player is { } player) player.Rate = _rate;
	}

	private void OnTimelineValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
	{
		if (_suppressTimeline || _player is not { } player) return;

		var position = TimeSpan.FromSeconds(e.NewValue);
		UpdateTimeText(position, player.Duration);
		// Exact even while dragging: a render's keyframes are a second apart, so keyframes would show the picture jump by seconds,
		// and the videos are compared frame for frame. One seek runs at a time and the newest position wins (RequestSeek).
		RequestSeek(position, SeekAccuracy.Exact);
	}

	/// <summary>One seek at a time, the newest position the pointer reached: the ones in between would only be decoded to be replaced.</summary>
	private void RequestSeek(TimeSpan position, SeekAccuracy accuracy)
	{
		_pendingSeek = (position, accuracy);
		if (!_seekRunning) _ = RunAsync(DrainSeeksAsync);
	}

	private async Task DrainSeeksAsync()
	{
		_seekRunning = true;
		try
		{
			while (_pendingSeek is { } next && _player is { } player)
			{
				_pendingSeek = null;
				await player.SeekAsync(next.Position, next.Accuracy);
			}
		}
		finally
		{
			_seekRunning = false;
		}
	}

	/// <summary>A drag of the playhead: playback pauses until it ends, then goes on from where it was dropped.</summary>
	private void OnScrubStarted()
	{
		_resumeAfterScrub = _player is { IsPlaying: true };
		if (_resumeAfterScrub) _ = RunAsync(TogglePlayAsync);
	}

	private void OnScrubEnded()
	{
		_pendingSeek = null;
		bool resume = _resumeAfterScrub;
		_resumeAfterScrub = false;
		if (_player is not { } player) return;

		var position = TimeSpan.FromSeconds(Timeline.Value);
		_ = RunAsync(async () =>
		{
			await player.SeekAsync(position, SeekAccuracy.Exact);
			if (resume) await TogglePlayAsync();
		});
	}

	private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
	{
		if (_player is null || e.Source is ComboBox) return;

		switch (e.Key)
		{
			case Key.Space:
				e.Handled = true;
				await RunAsync(TogglePlayAsync);
				break;
			case Key.Left:
				e.Handled = true;
				await StepAsync(-1);
				break;
			case Key.Right:
				e.Handled = true;
				await StepAsync(1);
				break;
			case Key.Home:
				e.Handled = true;
				await GoToAsync(false);
				break;
			case Key.End:
				e.Handled = true;
				await GoToAsync(true);
				break;
			case Key.OemPlus or Key.Add:
				e.Handled = true;
				ZoomBy(KeyZoomStep);
				break;
			case Key.OemMinus or Key.Subtract:
				e.Handled = true;
				ZoomBy(1 / KeyZoomStep);
				break;
			case Key.D0 or Key.NumPad0:
				e.Handled = true;
				SetZoom(CompareZoom.Whole);
				break;
		}
	}

	private void OnZoomInClick(object? sender, RoutedEventArgs e)
	{
		ZoomBy(KeyZoomStep);
	}

	private void OnZoomOutClick(object? sender, RoutedEventArgs e)
	{
		ZoomBy(1 / KeyZoomStep);
	}

	private void OnZoomResetClick(object? sender, RoutedEventArgs e)
	{
		SetZoom(CompareZoom.Whole);
	}

	/// <summary>Zoomed into or out of the middle of the pictures.</summary>
	private void ZoomBy(double factor)
	{
		SetZoom(_zoom.ZoomAt(factor, 0.5, 0.5));
	}

	private void SetZoom(CompareZoom zoom)
	{
		_zoom = zoom;
		ApplyZoom();
	}

	/// <summary>Every tile looks at the same part of its picture: the same scale, and the shift in that picture's own pixels.</summary>
	private void ApplyZoom()
	{
		bool zoomed = _zoom.Scale > CompareZoom.MinScale;
		foreach (TileZoomView view in _zoomViews)
		{
			view.Picture.Cursor = zoomed ? MoveCursor : null;
			Size picture = view.PictureSize();
			view.Scale.ScaleX = _zoom.Scale;
			view.Scale.ScaleY = _zoom.Scale;
			view.Shift.X = -(_zoom.CenterX - 0.5) * picture.Width * _zoom.Scale;
			view.Shift.Y = -(_zoom.CenterY - 0.5) * picture.Height * _zoom.Scale;
		}

		ZoomText.Text = string.Format(Strings.ComparePlay_Zoom, _zoom.Scale.ToString("0.#", CultureInfo.InvariantCulture));
		ZoomResetButton.IsEnabled = zoomed;
		ZoomOutButton.IsEnabled = zoomed;
		ZoomInButton.IsEnabled = _zoom.Scale < CompareZoom.MaxScale;
	}

	/// <summary>
	///     On every tile at once: the wheel zooms, keeping the point under the pointer where it is; with Shift it moves the view across,
	///     with Alt down and up (a sideways touchpad swipe moves it across too). Dragging moves it as well.
	/// </summary>
	private void OnPictureWheel(TileZoomView view, PointerWheelEventArgs e)
	{
		Size picture = view.PictureSize();
		if (picture.Width <= 0 || picture.Height <= 0) return;

		if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
		{
			SetZoom(_zoom.Pan(e.Delta.Y * WheelPanStep, 0));
		}
		else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
		{
			SetZoom(_zoom.Pan(0, e.Delta.Y * WheelPanStep));
		}
		else if (Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
		{
			SetZoom(_zoom.Pan(e.Delta.X * WheelPanStep, 0));
		}
		else
		{
			Point at = view.FractionOf(e.GetPosition(view.Picture));
			SetZoom(_zoom.ZoomAt(Math.Pow(WheelZoomStep, e.Delta.Y), at.X, at.Y));
		}

		e.Handled = true;
	}

	private void OnPicturePressed(TileZoomView view, PointerPressedEventArgs e)
	{
		PointerPointProperties pressed = e.GetCurrentPoint(view.Picture).Properties;
		if (!pressed.IsLeftButtonPressed && !pressed.IsMiddleButtonPressed) return;

		_pan = (view, e.GetPosition(view.Picture));
		e.Pointer.Capture(view.Picture);
	}

	/// <summary>A drag in one tile moves them all - with the left button or the wheel pressed.</summary>
	private void OnPictureMoved(TileZoomView view, PointerEventArgs e)
	{
		if (_pan is not { } pan || pan.View != view) return;

		Size picture = view.PictureSize();
		Point at = e.GetPosition(view.Picture);
		if (picture.Width > 0 && picture.Height > 0) SetZoom(_zoom.Pan((at.X - pan.At.X) / picture.Width, (at.Y - pan.At.Y) / picture.Height));
		_pan = (view, at);
	}

	private void OnPictureReleased(TileZoomView view, PointerReleasedEventArgs e)
	{
		if (_pan?.View != view) return;

		_pan = null;
		e.Pointer.Capture(null);
	}

	private void UpdatePosition()
	{
		if (_player is not { } player) return;

		TimeSpan position = player.Position;
		_suppressTimeline = true;
		Timeline.Value = position.TotalSeconds;
		_suppressTimeline = false;
		UpdateTimeText(position, player.Duration);
	}

	private void UpdateTimeText(TimeSpan position, TimeSpan duration)
	{
		TimeText.Text = $"{Format(position)} / {Format(duration)}";
	}

	private static string Format(TimeSpan time)
	{
		return time.ToString(@"mm\:ss\.ff", CultureInfo.InvariantCulture);
	}

	private void SetPlayingIcon(bool playing)
	{
		PlayPauseIcon.Data = playing ? Icons.Pause : Icons.Play;
	}

	/// <summary>An operation of the player that failed is logged - the window stays as it was rather than crashing the program.</summary>
	private static async Task RunAsync(Func<Task> operation)
	{
		try
		{
			await operation();
		}
		catch (Exception ex)
		{
			AppLogger.Error(ex, "Playback comparison failed.");
		}
	}
}

/// <summary>A tile's picture area and the transform that zooms it - the picture is drawn aspect-correct inside the area.</summary>
internal sealed record TileZoomView(Panel Picture, ScaleTransform Scale, TranslateTransform Shift, double Aspect)
{
	/// <summary>The picture's size in the area: as wide as fits at its aspect ratio.</summary>
	public Size PictureSize()
	{
		double width = Math.Min(Picture.Bounds.Width, Picture.Bounds.Height * Aspect);
		return new Size(width, width / Aspect);
	}

	/// <summary>A point of the area as fractions of the picture (0 to 1 across and down), whose corner is where the picture starts in the area.</summary>
	public Point FractionOf(Point point)
	{
		Size picture = PictureSize();
		return new Point((point.X - (Picture.Bounds.Width - picture.Width) / 2) / picture.Width,
			(point.Y - (Picture.Bounds.Height - picture.Height) / 2) / picture.Height);
	}
}
