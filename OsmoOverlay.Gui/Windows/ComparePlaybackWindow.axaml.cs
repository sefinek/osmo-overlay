using System.Globalization;
using Avalonia;
using Avalonia.Controls;
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

namespace OsmoOverlay.Gui;

/// <summary>
///     Exported videos played side by side on one clock (ComparePlayer) - to see what a release of the program changed in
///     the same frames of the same recording. The window only shows and steers; the tiles' decoding and timing are the
///     player's. Changing the set of videos opens every one again.
/// </summary>
public partial class ComparePlaybackWindow : Window
{
	private const int MaxFiles = 6;

	// The widest a picture is decoded: tiles show it smaller, and four 4K decoders at full size would only slow each other down.
	private const int TileDecodeWidth = 960;

	private static readonly double[] Rates = [0.25, 0.5, 1, 2];

	private readonly List<string> _paths = [];
	private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
	private ComparePlayer? _player;
	private int _generation;
	private bool _suppressTimeline;
	private bool _scrubbing;
	private bool _resumeAfterScrub;
	private bool _loop;
	private double _rate = 1;

	public ComparePlaybackWindow()
	{
		InitializeComponent();
		SpeedCombo.ItemsSource = Rates.Select(r => r.ToString("0.##", CultureInfo.InvariantCulture) + "x").ToList();
		SpeedCombo.SelectedIndex = Array.IndexOf(Rates, 1.0);
		Timeline.ScrubStarted += OnScrubStarted;
		Timeline.ScrubEnded += OnScrubEnded;
		_timer.Tick += (_, _) => UpdatePosition();
		KeyDown += OnWindowKeyDown;
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

		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
				string.Format(Strings.ComparePlay_OpenFailed, string.Join("\n", failures.Select(f => $"{System.IO.Path.GetFileName(f.Path)}: {f.Reason}"))),
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
		// The frame is lent for the call, from a decoder's thread - copied here, shown on the UI thread, which a view needs.
		player.StillReady += (index, frame) =>
		{
			var copy = new ComposedPreviewFrame(frame.Position, frame.Bgra.ToArray(), frame.Width, frame.Height);
			Dispatcher.UIThread.Post(() =>
			{
				if (_player == player) views[index].ShowStill(copy);
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

		bool markersDiffer = player.Tiles.Select(t => t.Marker).Distinct().Count() > 1;
		var views = new VideoView[count];
		for (int i = 0; i < count; i++)
		{
			CompareTile tile = player.Tiles[i];
			var view = new VideoView { Width = tile.Width, Height = tile.Height };
			views[i] = view;

			Border tileControl = BuildTile(tile, view, markersDiffer, player, i);
			Grid.SetColumn(tileControl, i % CompareLayout.Columns(count));
			Grid.SetRow(tileControl, i / CompareLayout.Columns(count));
			TilesGrid.Children.Add(tileControl);
		}

		return views;
	}

	private Border BuildTile(CompareTile tile, VideoView view, bool markersDiffer, ComparePlayer player, int index)
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
			Text = System.IO.Path.GetFileName(tile.Path),
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

		var marker = new TextBlock
		{
			Text = MarkerText(tile.Marker),
			FontSize = 12,
			Foreground = tile.Marker is null ? Palette.TextMuted : markersDiffer ? Palette.Warning : Palette.TextPrimary,
			TextWrapping = TextWrapping.Wrap
		};
		var info = new TextBlock
		{
			Text = string.Format(Strings.ComparePlay_Info, tile.Source.Video.Width, tile.Source.Video.Height,
				tile.Fps.ToString("0.###", CultureInfo.InvariantCulture)),
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

		var footer = new StackPanel { Margin = new Thickness(10, 6, 10, 8), Spacing = 2, Children = { marker, info, error } };
		var picture = new Viewbox { Stretch = Stretch.Uniform, Child = view, Margin = new Thickness(4, 4, 4, 0) };
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

	private static string MarkerText(ExportMarkerInfo? marker)
	{
		return marker is null
			? Strings.ComparePlay_NoMarker
			: string.Format(Strings.ComparePlay_Marker, marker.Version, marker.TelemetryVersion,
				marker.Orientation == OrientationSource.Quaternion ? Strings.ComparePlay_TiltOrientation : Strings.ComparePlay_TiltAccelerometer);
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
		if (_player is { } player) await RunAsync(() => player.SeekAsync(TimeSpan.Zero, SeekAccuracy.Exact));
		UpdatePosition();
	}

	private async void OnGoToEndClick(object? sender, RoutedEventArgs e)
	{
		if (_player is { } player) await RunAsync(() => player.SeekAsync(player.Duration, SeekAccuracy.Exact));
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
		// Keyframes while dragging keep up with the pointer; the exact frame follows on release.
		_ = RunAsync(() => player.SeekAsync(position, _scrubbing ? SeekAccuracy.Keyframe : SeekAccuracy.Exact));
	}

	/// <summary>A drag of the playhead: playback pauses until it ends, then goes on from where it was dropped.</summary>
	private void OnScrubStarted()
	{
		_scrubbing = true;
		_resumeAfterScrub = _player is { IsPlaying: true };
		if (_resumeAfterScrub) _ = RunAsync(TogglePlayAsync);
	}

	private void OnScrubEnded()
	{
		_scrubbing = false;
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
				OnGoToStartClick(null, new RoutedEventArgs());
				break;
			case Key.End:
				e.Handled = true;
				OnGoToEndClick(null, new RoutedEventArgs());
				break;
		}
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
