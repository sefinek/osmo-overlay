using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Preview;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Gui;

public partial class SpeedCalibrationPreviewWindow : Window
{
	private readonly PreviewPlayer _player = new();
	private readonly CancellationTokenSource _loadCancellation = new();
	private readonly string _path;
	private IReadOnlyList<DerivedFrame>? _frames;
	private TimeSpan _position;
	private bool _updatingTimeline;
	private bool _closed;
	private bool _ready;
	private readonly Stopwatch _loadWatch = new();
	private readonly DispatcherTimer _loadTimer = new() { Interval = TimeSpan.FromSeconds(1) };
	private readonly DispatcherTimer _correctionTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
	private CancellationTokenSource? _correctionCancellation;
	private string _loadStage = "";
	private bool _loading;
	private List<Peak> _peaks = [];
	private readonly List<Button> _peakButtons = [];
	private double _fps;

	public bool Accepted { get; private set; }
	public double Percent => SpeedEditor.Percent;
	public double? RealSpeedValue => SpeedEditor.RealSpeedValue;
	public double? GpsSpeedValue => SpeedEditor.GpsSpeedValue;
	public double? CruisingSpeedKmh { get; private set; }

	public static async Task ShowForEditorAsync(Window owner, SpeedCalibrationEditor editor)
	{
		IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = Strings.Main_PickRecordings,
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType(Strings.Main_CameraRecordings) { Patterns = ["*.mp4", "*.insv", "*.lrv"] }]
		});
		if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

		var preview = new SpeedCalibrationPreviewWindow(path, editor.Percent, editor.RealSpeedValue);
		await preview.ShowDialog(owner);
		if (!preview.Accepted) return;
		editor.Load(preview.Percent, preview.CruisingSpeedKmh);
		editor.SetComparisonSpeeds(preview.RealSpeedValue, preview.GpsSpeedValue);
	}

	public SpeedCalibrationPreviewWindow() : this("", 0)
	{
	}

	public SpeedCalibrationPreviewWindow(string path, double percent, double? actualSpeed = null)
	{
		InitializeComponent();
		_path = path;
		RecordingName.Text = Path.GetFileName(path);
		SpeedEditor.Load(percent, null);
		SpeedEditor.SetComparisonSpeeds(actualSpeed, null);
		SpeedEditor.PercentChanged += _ =>
		{
			if (!_ready) return;
			QueueSpeedCorrection();
			ShowPosition(_position);
			ShowPeaks();
		};
		_player.FrameReady += frame =>
		{
			if (_closed) return;
			PreviewVideo.ShowStill(frame);
			ShowPosition(frame.Position);
		};
		PreviewVideo.PlaybackFrameShown += (position, _) => { if (!_closed) ShowPosition(position); };
		_player.PlaybackStarted += () =>
		{
			PreviewVideo.StartPlayback(_player.Frames);
			ShowPlayingState(true);
		};
		_player.PlaybackStopped += () => ShowPlayingState(false);
		_player.Message += message => Dispatcher.UIThread.Post(() =>
		{
			if (_closed) return;
			StatusText.Text = message;
			AppLogger.Warn(message);
		});
		Timeline.PropertyChanged += (_, e) =>
		{
			if (e.Property == RangeBase.ValueProperty && !_updatingTimeline && _ready)
			{
				_player.Pause();
				_player.RequestSeek(TimeSpan.FromSeconds(Timeline.Value));
			}
		};
		KeyDown += (_, e) =>
		{
			if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
			if (e.Key == Key.Space && _ready && FocusManager?.GetFocusedElement() is not TextBox)
			{
				_player.TogglePlayPause(_position);
				e.Handled = true;
			}
			else if (_ready && FocusManager?.GetFocusedElement() is not TextBox)
			{
				switch (e.Key)
				{
					case Key.Left: OnPreviousClick(null, new RoutedEventArgs()); break;
					case Key.Right: OnNextClick(null, new RoutedEventArgs()); break;
					case Key.Home: SeekTo(TimeSpan.Zero); break;
					case Key.End: SeekTo(TimeSpan.FromSeconds(Timeline.Maximum)); break;
					default: return;
				}
				e.Handled = true;
			}
		};
		Opened += async (_, _) => { if (!string.IsNullOrEmpty(_path)) await LoadRecordingAsync(); };
		_correctionTimer.Tick += async (_, _) =>
		{
			_correctionTimer.Stop();
			await ApplySpeedCorrectionAsync();
		};
		_loadTimer.Tick += (_, _) =>
		{
			ShowLoadingStatus();
			if ((int)_loadWatch.Elapsed.TotalSeconds % 10 == 0) AppLogger.Notify(StatusText.Text ?? _loadStage);
		};
		Closed += (_, _) =>
		{
			_closed = true;
			_ready = false;
			if (_loading) _loadCancellation.Cancel();
			else _loadCancellation.Dispose();
			_loadTimer.Stop();
			_correctionTimer.Stop();
			_correctionCancellation?.Cancel();
			_player.Dispose();
		};
	}

	private async Task LoadRecordingAsync()
	{
		_loading = true;
		CancellationToken ct = _loadCancellation.Token;
		_loadWatch.Start();
		_loadTimer.Start();
		SetLoadStage(Strings.Calibration_PreviewLoading);
		try
		{
			(FileSummary? summary, string? problem) = await Task.Run(() => FileSummaryReader.Read([_path], onProgress: stage =>
			{
				ct.ThrowIfCancellationRequested();
				string message = stage switch
				{
					FileSummaryReadStage.Cache => Strings.Calibration_LoadCache,
					FileSummaryReadStage.Probe => Strings.Calibration_LoadProbe,
					FileSummaryReadStage.Extract => Strings.Calibration_LoadTelemetry,
					FileSummaryReadStage.Process => Strings.Calibration_LoadSpeeds,
					FileSummaryReadStage.CameraModel => Strings.Calibration_LoadCamera,
					_ => Strings.Calibration_LoadSave
				};
				AppLogger.Notify(message);
				Dispatcher.UIThread.Post(() => { if (!_closed && _loading) { _loadStage = message; ShowLoadingStatus(); } });
			}), ct);
			if (_closed) return;
			if (summary is null) throw new InvalidOperationException(problem);
			if (summary.TelemetryFrames is not { Count: > 0 } raw || !OverlayAvailability.Of(raw, false, summary.CameraFormat).GpsFix)
			{
				StatusText.Text = Strings.Calibration_PreviewNoGps;
				AppLogger.Warn(StatusText.Text);
				return;
			}

			OverlaySettings settings = OverlaySettingsStore.Load() with { SpeedCorrectionPercent = 0, ShowRouteIntro = false, ShowWatermark = false };
			SetLoadStage(Strings.Calibration_LoadSpeeds);
			(IReadOnlyList<DerivedFrame> frames, double cruising, List<Peak> peaks) = await Task.Run(() =>
			{
				IReadOnlyList<DerivedFrame> processed = !settings.SmoothGpsMotion && summary.DerivedFrames is { Count: > 0 } derived
					? derived : TelemetryProcessor.Process(raw, summary.CameraFormat, settings.SmoothGpsMotion, 0);
				ct.ThrowIfCancellationRequested();
				double estimatedCruising = SpeedCalibration.CruisingSpeedKmh(processed);
				List<Peak> topSpeeds = KeyMoments.FindTop(processed, PeakKind.TopSpeed, summary.Video.Fps, summary.TotalFrameCount, 3);
				return (processed, estimatedCruising, topSpeeds);
			}, ct);
			if (_closed) return;
			_frames = frames;
			CruisingSpeedKmh = cruising;
			SpeedEditor.Load(Percent, CruisingSpeedKmh);
			_fps = summary.Video.Fps;
			_peaks = peaks;
			SetLoadStage(Strings.Calibration_LoadDecoder);
			double scale = Math.Min(1, Math.Min(1280.0 / summary.Video.Width, 720.0 / summary.Video.Height));
			int width = Math.Max(2, (int)(summary.Video.Width * scale) & ~1);
			int height = Math.Max(2, (int)(summary.Video.Height * scale) & ~1);
			PreviewVideo.Width = width;
			PreviewVideo.Height = height;
			await _player.OpenAsync(summary, width, height, settings,
				[new SpeedGaugeElement { X = OverlayElementBounds.ReferenceWidth - 650, Y = 1500, Scale = 1.75f }], _frames);
			if (_closed) return;
			Timeline.Maximum = Math.Max(0, _player.Duration.TotalSeconds - 1 / summary.Video.Fps);
			double initialPercent = Percent;
			await _player.SetSpeedCorrectionAsync(initialPercent, ct);
			if (_closed) return;
			_ready = true;
			if (Percent != initialPercent) QueueSpeedCorrection();
			StatusText.Text = Strings.Calibration_PreviewHint;
			AppLogger.Notify(string.Format(Strings.Calibration_LoadReady, Path.GetFileName(_path), _loadWatch.Elapsed.ToString(@"mm\:ss")));
			TransportPanel.IsEnabled = Timeline.IsEnabled = UseButton.IsEnabled = true;
			CreatePeakButtons();
			ShowPeaks();
		}
		catch (Exception ex)
		{
			if (_closed) return;
			_player.Close();
			StatusText.Text = string.Format(Strings.Calibration_PreviewFailed, ex.Message);
			AppLogger.Error(ex, StatusText.Text);
		}
		finally
		{
			_loading = false;
			_loadTimer.Stop();
			_loadWatch.Stop();
			if (_closed) _loadCancellation.Dispose();
			if (!_closed)
			{
				LoadingProgress.IsVisible = false;
				if (_ready) Dispatcher.UIThread.Post(FitWindowToVideo, DispatcherPriority.Loaded);
			}
		}
	}

	private void FitWindowToVideo()
	{
		if (_closed || (Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen) return;
		double decorationHeight = Math.Max(0, (FrameSize?.Height ?? ClientSize.Height) - ClientSize.Height);
		double maxWidth = Math.Max(1, screen.WorkingArea.Width / DesktopScaling - 40);
		double maxHeight = Math.Max(1, screen.WorkingArea.Height / DesktopScaling - decorationHeight - 40);
		MaxHeight = maxHeight;
		MinWidth = Math.Min(MinWidth, maxWidth);
		Width = Math.Min(Width, maxWidth);
		ContentGrid.Measure(new Size(Width, double.PositiveInfinity));
		double fixedHeight = ContentGrid.DesiredSize.Height - VideoBox.DesiredSize.Height;
		double videoHeight = Math.Max(1, maxHeight - fixedHeight);
		VideoBox.MaxHeight = videoHeight;
		if (VideoBox.DesiredSize.Height > videoHeight)
		{
			double fixedWidth = Width - VideoBox.DesiredSize.Width;
			double targetWidth = Math.Min(maxWidth, fixedWidth + videoHeight * PreviewVideo.Width / PreviewVideo.Height);
			MinWidth = Math.Min(MinWidth, targetWidth);
			Width = targetWidth;
		}
	}

	private void QueueSpeedCorrection()
	{
		_correctionCancellation?.Cancel();
		_correctionTimer.Stop();
		_correctionTimer.Start();
	}

	private async Task ApplySpeedCorrectionAsync()
	{
		if (!_ready || _closed) return;
		var cancellation = new CancellationTokenSource();
		_correctionCancellation = cancellation;
		try
		{
			await _player.SetSpeedCorrectionAsync(Percent, cancellation.Token);
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			if (_closed) return;
			StatusText.Text = string.Format(Strings.Calibration_PreviewFailed, ex.Message);
			AppLogger.Error(ex, StatusText.Text);
		}
		finally
		{
			if (ReferenceEquals(_correctionCancellation, cancellation)) _correctionCancellation = null;
			cancellation.Dispose();
		}
	}

	private void SetLoadStage(string message)
	{
		_loadStage = message;
		ShowLoadingStatus();
		AppLogger.Notify(message);
	}

	private void ShowLoadingStatus()
	{
		StatusText.Text = string.Format(Strings.Calibration_LoadProgress, _loadStage, _loadWatch.Elapsed.ToString(@"mm\:ss"));
	}

	private void CreatePeakButtons()
	{
		SpeedPeakButtons.Children.Clear();
		_peakButtons.Clear();
		SpeedPeaksPanel.IsVisible = _peaks.Count > 0;
		foreach (Peak peak in _peaks)
		{
			var position = TimeSpan.FromSeconds(peak.Frame / _fps);
			var button = new Button
			{
				Margin = new Thickness(0, 0, 8, 4)
			};
			button.Click += (_, _) => SeekTo(position);
			_peakButtons.Add(button);
			SpeedPeakButtons.Children.Add(button);
		}
	}

	private void ShowPeaks()
	{
		for (int i = 0; i < _peakButtons.Count; i++)
		{
			Peak peak = _peaks[i];
			var position = TimeSpan.FromSeconds(peak.Frame / _fps);
			Button button = _peakButtons[i];
			double corrected = SpeedCalibration.Corrected(peak.Value, Percent);
			button.Content = $"{corrected:0.0} km/h · {position:hh\\:mm\\:ss}";
			ToolTip.SetTip(button, string.Format(Strings.Calibration_PreviewReading, peak.Value, corrected));
		}
	}

	private void SeekTo(TimeSpan position)
	{
		if (!_ready) return;
		_player.Pause();
		_player.RequestSeek(TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Timeline.Maximum)));
	}

	private void ShowPlayingState(bool playing)
	{
		PlayPauseIcon.Data = playing ? Icons.Pause : Icons.Play;
		ToolTip.SetTip(PlayButton, playing ? Strings.Transport_Pause : Strings.Main_PlaySpace);
	}

	private void ShowPosition(TimeSpan position)
	{
		_position = position;
		_updatingTimeline = true;
		Timeline.Value = position.TotalSeconds;
		_updatingTimeline = false;
		PositionText.Text = $"{position:hh\\:mm\\:ss} / {_player.Duration:hh\\:mm\\:ss}";
		if (_frames is { Count: > 0 } frames)
		{
			double measured = TelemetryProcessor.FindNearest(frames, position.TotalSeconds).SpeedKmh;
			SpeedReading.Text = string.Format(Strings.Calibration_PreviewReading, measured, SpeedCalibration.Corrected(measured, Percent));
		}
	}

	private void OnPlayClick(object? sender, RoutedEventArgs e) => _player.TogglePlayPause(_position);
	private void OnStartClick(object? sender, RoutedEventArgs e) => SeekTo(TimeSpan.Zero);
	private void OnEndClick(object? sender, RoutedEventArgs e) => SeekTo(TimeSpan.FromSeconds(Timeline.Maximum));
	private void OnPreviousClick(object? sender, RoutedEventArgs e) => SeekTo(_position - TimeSpan.FromSeconds(1 / _fps));
	private void OnNextClick(object? sender, RoutedEventArgs e) => SeekTo(_position + TimeSpan.FromSeconds(1 / _fps));
	private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
	private void OnUseClick(object? sender, RoutedEventArgs e) { Accepted = true; Close(); }
}
