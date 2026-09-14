using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Preview;
using SkiaSharp;
using RenderOptions = OsmoOverlay.Core.RenderOptions;

namespace OsmoOverlay.Gui;

public partial class MainWindow : Window
{
	private const int PreviewMaxWidth = 960;
	private readonly PreviewPlayer _previewPlayer = new();
	private string _activePresetId = "";

	private CancellationTokenSource? _cts;
	private string _detectedEncoder = "";
	private Point _dragAnchorOffset;
	private OverlayElementType? _draggingElementType;

	private List<OverlayPreset> _overlayPresets = [];
	private UiPhase _phase = UiPhase.Idle;
	private WriteableBitmap? _previewBitmap;
	private bool _sliderDragInProgress;
	private FileSummary? _summary;
	private bool _suppressOverlayEvents;
	private bool _suppressSliderEvent;

	public MainWindow()
	{
		InitializeComponent();
		Opened += OnWindowOpened;

		_previewPlayer.FrameReady += OnPreviewFrameReady;
		_previewPlayer.PlaybackStopped += OnPreviewPlaybackStopped;
		_previewPlayer.Message += AppendLog;
	}

	// FirstOrDefault, not First: there's a narrow window right after picking a file where
	// _summary is already set but LoadOverlayPresets (an earlier await) hasn't finished yet, so a
	// pointer click on the drag canvas in that gap must not crash on an empty/stale preset list.
	private List<OverlayElement> ActiveElements =>
		_overlayPresets.FirstOrDefault(p => p.Id == _activePresetId)?.Elements ?? [];

	private async void OnWindowOpened(object? sender, EventArgs e)
	{
		Opened -= OnWindowOpened;

		IReadOnlyList<ExternalTool> missing = await DependencyChecker.FindMissingAsync(RequiredTools.All);
		if (missing.Count == 0) return;

		await new DependencyPromptWindow(missing).ShowDialog(this);
	}

	private async void OnPickInputClick(object? sender, RoutedEventArgs e)
	{
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Select a DJI Osmo Action recording",
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType("MP4 video") { Patterns = ["*.mp4", "*.MP4"] }]
		});

		if (files.Count == 0) return;

		var inputPath = files[0].Path.LocalPath;
		InputPathBox.Text = inputPath;

		OutputPathBox.Text = RenderOptions.DefaultOutputPath(inputPath);

		ClosePreview();
		SetPhase(UiPhase.Idle);
		ActionButton.IsEnabled = true;
	}

	private async void OnPickOutputClick(object? sender, RoutedEventArgs e)
	{
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = "Save as",
			SuggestedFileName = string.IsNullOrWhiteSpace(OutputPathBox.Text)
				? "output_overlay.mp4"
				: Path.GetFileName(OutputPathBox.Text),
			DefaultExtension = "mp4",
			FileTypeChoices = [new FilePickerFileType("MP4 video") { Patterns = ["*.mp4"] }]
		});

		if (file is not null)
			OutputPathBox.Text = file.Path.LocalPath;
	}

	private async void OnActionClick(object? sender, RoutedEventArgs e)
	{
		switch (_phase)
		{
			case UiPhase.Idle:
				await RunGetSummaryAsync();
				break;
			case UiPhase.SummaryReady:
				await RunRenderAsync();
				break;
		}
	}

	private async Task RunGetSummaryAsync()
	{
		var inputPath = InputPathBox.Text ?? "";
		if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
			return;

		ClosePreview();
		SetPhase(UiPhase.LoadingSummary);
		LogBox.Text = "";
		AppendLog($"Input: {inputPath}");
		var cachePath = FileSummaryReader.GetCachePath(inputPath);
		AppendLog($"Cache file: {cachePath} (format v{FileSummaryReader.CacheFormatVersion})");
		AppendLog("Probing source file (ffprobe)...");
		AppendLog("Extracting telemetry (djmd stream)... falls back to exiftool if the raw layout doesn't match, unless cached.");

		try
		{
			FileSummary summary = await Task.Run(() => FileSummaryReader.Read(inputPath));

			AppendLog(summary.FromCache
				? "Cache hit: using cached analysis (file unchanged since last run)."
				: "Cache miss: analysis recomputed and saved to cache.");

			AppendLog(
				$"ffprobe: {summary.Video.CodecName} {summary.Video.Profile}, {summary.Video.Width}x{summary.Video.Height}, " +
				$"{FormatFps(summary.Video.Fps)} fps, {summary.Video.PixFmt}, ~{summary.Video.BitRate / 1_000_000} Mbps");
			AppendLog(
				$"Color: {summary.Video.ColorPrimaries ?? "?"} / {summary.Video.ColorTransfer ?? "?"} / " +
				$"{summary.Video.ColorSpace ?? "?"} ({summary.Video.ColorRange ?? "?"})");
			AppendLog($"Duration: {TimeSpan.FromSeconds(summary.DurationSeconds):hh\\:mm\\:ss}, size: {FormatBytes(summary.FileSizeBytes)}");
			AppendLog(summary.Audio is { } audio
				? $"Audio: {audio.CodecName}, {audio.SampleRate} Hz, {audio.Channels}ch"
				: "Audio: none");

			AppendLog($"Camera model: {summary.CameraModel ?? "unknown"}");
			AppendLog(summary.Telemetry is { } t
				? $"Telemetry stream detected (djmd) - {summary.TelemetryFrames?.Count ?? 0} raw samples, " +
				  $"{summary.DerivedFrames?.Count ?? 0} derived frames."
				: "No telemetry stream found - this file cannot be rendered.");

			if (summary.TelemetryFrames is { Count: > 0 } rawFrames)
			{
				var withGpsSpeed = rawFrames.Count(f => f.GpsSpeedMs is not null);
				AppendLog(withGpsSpeed > 0
					? $"GPS-measured speed: {withGpsSpeed}/{rawFrames.Count} frames (protobuf djmd velocity); " +
					  $"{rawFrames.Count - withGpsSpeed} fall back to derived speed."
					: "GPS-measured speed: not available for this file - using derived speed for all frames.");

				var withCameraSettings = rawFrames.Count(f => f.Iso is not null);
				AppendLog(withCameraSettings == rawFrames.Count
					? "Telemetry source: native djmd decoder (ISO/shutter/color temp all present, exiftool not needed)."
					: withCameraSettings > 0
						? $"Telemetry source: native djmd decoder, partial camera settings ({withCameraSettings}/{rawFrames.Count})."
						: "Telemetry source: exiftool fallback (native djmd decode failed or ISO/shutter/CT unavailable).");
			}

			if (summary.Telemetry is { } tele)
			{
				AppendLog(
					$"Telemetry summary: {tele.TotalDistanceMeters / 1000.0:0.00} km, " +
					$"max speed {tele.MaxSpeedKmh:0.#} km/h, altitude {tele.MinAltitudeMeters:0}-{tele.MaxAltitudeMeters:0} m, " +
					$"max G {tele.MaxGForce:0.00}");
				AppendLog(tele.RecordedAtUtc is { } recordedUtc
					? $"Recorded at: {recordedUtc.ToLocalFromUtc():yyyy-MM-dd HH:mm:ss} (local)"
					: "Recorded at: unknown (no GPS timestamp in telemetry)");
			}

			AppendLog("Checking NVENC availability...");
			var encoder = await Task.Run(FfmpegPipeline.SelectVideoEncoder);
			AppendLog($"Using encoder: {encoder}" + (encoder == "libx265" ? " (NVENC unavailable - CPU)" : " (GPU)"));

			_summary = summary;
			_detectedEncoder = encoder;

			PopulateInputInfo(summary);
			PopulateTelemetryInfo(summary);
			PopulateOutputInfo(summary, encoder);

			SetPhase(UiPhase.SummaryReady);
			ActionButton.IsEnabled = summary.HasTelemetry;

			if (summary.HasTelemetry)
				await OpenPreviewAsync(inputPath, summary);
		}
		catch (Exception ex)
		{
			AppendLog($"Could not read file info: {ex.Message}");
			SetPhase(UiPhase.Idle);
			ActionButton.IsEnabled = true;
		}
	}

	private async Task RunRenderAsync()
	{
		var inputPath = InputPathBox.Text ?? "";
		var outputPath = OutputPathBox.Text ?? "";

		if (string.IsNullOrWhiteSpace(outputPath))
		{
			AppendLog("Enter an output file path.");
			return;
		}

		int? frameLimit = FrameLimitBox.Value is { } v && v > 0 ? (int)v : null;

		_cts = new CancellationTokenSource();
		SetPhase(UiPhase.Rendering);
		LogBox.Text = "";
		Progress.Value = 0;

		var progress = new Progress<RenderStatus>(OnProgress);
		IReadOnlyList<OverlayElement>? layout = _overlayPresets.Count > 0 ? ActiveElements : null;
		var options = new RenderOptions(inputPath, outputPath, frameLimit, _detectedEncoder, _summary?.TelemetryFrames,
			Layout: layout);

		AppendLog($"Output: {outputPath}");
		AppendLog($"Encoder: {_detectedEncoder}, frame limit: {(frameLimit is { } fl ? fl.ToString() : "none")}");

		RenderResult result;
		try
		{
			result = await RenderJob.RunAsync(options, progress, _cts.Token);
		}
		finally
		{
			_cts = null;
		}

		AppendLog(result.Success
			? $"Done: {outputPath} (time: {result.Elapsed:hh\\:mm\\:ss})"
			: $"Error: {result.ErrorMessage}");

		SetPhase(UiPhase.SummaryReady);
		ActionButton.IsEnabled = true;
	}

	private void OnCancelClick(object? sender, RoutedEventArgs e)
	{
		_cts?.Cancel();
	}

	private void OnFrameLimitChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_summary is not null && _phase == UiPhase.SummaryReady)
			PopulateOutputInfo(_summary, _detectedEncoder);
	}

	private void SetPhase(UiPhase phase)
	{
		_phase = phase;

		var summaryVisible = phase is UiPhase.SummaryReady or UiPhase.Rendering;
		SummaryPanel.IsVisible = summaryVisible;
		OutputInfoCard.IsVisible = summaryVisible;
		FileInfoCard.IsVisible = phase == UiPhase.SummaryReady;
		TelemetryCard.IsVisible = phase == UiPhase.SummaryReady && _summary?.Telemetry is not null;
		OverlayCard.IsVisible = summaryVisible && _summary?.HasTelemetry == true;

		CancelButton.IsVisible = phase == UiPhase.Rendering;
		Progress.IsVisible = phase == UiPhase.Rendering;
		ActionButton.IsVisible = phase != UiPhase.Rendering;
		ActionButton.Content = phase == UiPhase.SummaryReady ? "Render" : "Get Summary";

		if (phase is UiPhase.LoadingSummary or UiPhase.Rendering)
			ActionButton.IsEnabled = false;
	}

	private void PopulateInputInfo(FileSummary summary)
	{
		InfoCamera.Text = summary.CameraModel ?? "Unknown";
		InfoResolution.Text = $"{summary.Video.Width}x{summary.Video.Height}";
		InfoFrameRate.Text = $"{FormatFps(summary.Video.Fps)} fps";
		InfoCodec.Text = string.IsNullOrEmpty(summary.Video.Profile)
			? summary.Video.CodecName
			: $"{summary.Video.CodecName} ({summary.Video.Profile})";
		InfoPixFmt.Text = summary.Video.PixFmt;
		InfoColor.Text =
			$"{summary.Video.ColorPrimaries ?? "?"} / {summary.Video.ColorTransfer ?? "?"} / {summary.Video.ColorSpace ?? "?"} ({summary.Video.ColorRange ?? "?"})";
		InfoBitrate.Text = $"{summary.Video.BitRate / 1_000_000.0:0.#} Mbps";
		InfoDuration.Text = TimeSpan.FromSeconds(summary.DurationSeconds).ToString(@"hh\:mm\:ss");
		InfoFileSize.Text = FormatBytes(summary.FileSizeBytes);

		InfoAudio.Text = summary.Audio is { } a
			? $"Audio: {a.CodecName}, {a.SampleRate} Hz, {a.Channels}ch"
			: "Audio: none";

		InfoTelemetry.Text = summary.HasTelemetry ? "Detected" : "Not found";
		InfoTelemetry.Foreground = summary.HasTelemetry
			? new SolidColorBrush(Color.Parse("#4CAF50"))
			: new SolidColorBrush(Color.Parse("#E5484D"));
		TelemetryPill.Background = summary.HasTelemetry
			? new SolidColorBrush(Color.Parse("#3D4CAF50"))
			: new SolidColorBrush(Color.Parse("#3DE5484D"));
	}

	private void PopulateTelemetryInfo(FileSummary summary)
	{
		if (summary.Telemetry is not { } t) return;

		TeleSamples.Text = $"{t.SampleCount}";
		TeleDuration.Text = TimeSpan.FromSeconds(t.DurationSeconds).ToString(@"hh\:mm\:ss");
		TeleDistance.Text = $"{t.TotalDistanceMeters / 1000.0:0.00} km";
		TeleMaxSpeed.Text = $"{t.MaxSpeedKmh:0.#} km/h";
		TeleAltitude.Text = $"{t.MinAltitudeMeters:0} - {t.MaxAltitudeMeters:0} m";
		TeleMaxG.Text = $"{t.MaxGForce:0.00} G";
		TeleRecordedAt.Text = t.RecordedAtUtc is { } utc ? utc.ToLocalFromUtc().ToString("yyyy-MM-dd HH:mm:ss") : "Unknown";
	}

	private void PopulateOutputInfo(FileSummary summary, string encoder)
	{
		var fps = summary.Video.Fps;
		int? frameLimit = FrameLimitBox.Value is { } v && v > 0 ? (int)v : null;
		var totalFrames = frameLimit ?? (int)Math.Ceiling(summary.DurationSeconds * fps);
		var outDurationSeconds = frameLimit is not null ? frameLimit.Value / fps : summary.DurationSeconds;

		OutCodec.Text = string.IsNullOrEmpty(summary.Video.Profile)
			? summary.Video.CodecName
			: $"{summary.Video.CodecName} ({summary.Video.Profile})";
		OutEncoder.Text = encoder + (encoder == "libx265" ? " (CPU)" : " (GPU)");
		OutResolution.Text = $"{summary.Video.Width}x{summary.Video.Height} @ {FormatFps(summary.Video.Fps)} fps";
		OutAudio.Text = summary.Audio is not null ? "Copied (no re-encode)" : "None";
		OutFrames.Text = $"{totalFrames} frames (~{TimeSpan.FromSeconds(outDurationSeconds):hh\\:mm\\:ss})";
		OutPath.Text = OutputPathBox.Text;
	}

	private static string FormatFps(double fps)
	{
		return fps.ToString("0.##");
	}

	private static string FormatBytes(long bytes)
	{
		var gb = bytes / 1_073_741_824.0;
		return gb >= 1 ? $"{gb:0.##} GB" : $"{bytes / 1_048_576.0:0.#} MB";
	}

	private void OnProgress(RenderStatus status)
	{
		AppendLog(status.Message);

		if (status.TotalFrames > 0)
			Progress.Value = 100.0 * status.CurrentFrame / status.TotalFrames;
	}

	private void AppendLog(string message)
	{
		LogBox.AppendLog(LogScroll, message);
	}

	private async Task OpenPreviewAsync(string inputPath, FileSummary summary)
	{
		ClosePreview();

		if (summary.DerivedFrames is not { Count: > 0 }) return;

		try
		{
			var scale = Math.Min(1.0, (double)PreviewMaxWidth / summary.Video.Width);
			var previewWidth = (int)(summary.Video.Width * scale) & ~1;
			var previewHeight = (int)(summary.Video.Height * scale) & ~1;

			_previewBitmap = new WriteableBitmap(new PixelSize(previewWidth, previewHeight), new Vector(96, 96),
				PixelFormat.Bgra8888, AlphaFormat.Opaque);
			PreviewImage.Source = _previewBitmap;

			await _previewPlayer.OpenAsync(inputPath, summary, previewWidth, previewHeight);
			LoadOverlayPresets(summary.Video.Width, summary.Video.Height);

			PreviewSlider.Maximum = _previewPlayer.Duration.TotalSeconds;
			PreviewPlaceholder.IsVisible = false;
			PlayPauseButton.IsEnabled = true;
			PreviewSlider.IsEnabled = true;
		}
		catch (Exception ex)
		{
			AppendLog($"Preview unavailable: {ex.Message}");
			ClosePreview();
		}
	}

	private void ClosePreview()
	{
		_previewPlayer.Close();
		PlayPauseButton.Content = "Play";
		PlayPauseButton.IsEnabled = false;
		PreviewSlider.IsEnabled = false;

		_previewBitmap = null;
		PreviewImage.Source = null;
		PreviewPlaceholder.IsVisible = true;
	}

	private void OnPreviewFrameReady(ComposedPreviewFrame frame)
	{
		if (_previewBitmap is null) return;

		using (ILockedFramebuffer fb = _previewBitmap.Lock())
		{
			Marshal.Copy(frame.Bgra, 0, fb.Address, frame.Bgra.Length);
		}

		PreviewImage.InvalidateVisual();

		if (!_sliderDragInProgress)
		{
			_suppressSliderEvent = true;
			PreviewSlider.Value = frame.Position.TotalSeconds;
			_suppressSliderEvent = false;
		}

		PreviewTimeText.Text = $"{FormatTime(frame.Position)} / {FormatTime(_previewPlayer.Duration)}";
	}

	private void OnPreviewPlaybackStopped()
	{
		PlayPauseButton.Content = "Play";
	}

	private static string FormatTime(TimeSpan t)
	{
		return t.ToString(@"mm\:ss");
	}

	private void OnPreviewSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
	{
		if (_suppressSliderEvent) return;

		_ = _previewPlayer.RequestSeekAsync(TimeSpan.FromSeconds(e.NewValue));
	}

	private void OnPreviewSliderPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		_sliderDragInProgress = true;
		_previewPlayer.BeginScrubDrag();
	}

	private void OnPreviewSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		_sliderDragInProgress = false;
		_previewPlayer.EndScrubDrag(TimeSpan.FromSeconds(PreviewSlider.Value));
	}

	private void OnPlayPauseClick(object? sender, RoutedEventArgs e)
	{
		var wasPlaying = _previewPlayer.IsPlaying;
		_previewPlayer.TogglePlayPause(TimeSpan.FromSeconds(PreviewSlider.Value));
		if (!wasPlaying) PlayPauseButton.Content = "Pause";
	}

	private void LoadOverlayPresets(int width, int height)
	{
		(_overlayPresets, _activePresetId) = OverlayPresetStore.Load(width, height);
		RefreshPresetComboBox();
		RefreshElementCheckboxes();
		_previewPlayer.SetLayout(ActiveElements);
	}

	private void SaveOverlayPresets()
	{
		OverlayPresetStore.Save(_overlayPresets, _activePresetId);
	}

	private void RefreshPresetComboBox()
	{
		_suppressOverlayEvents = true;
		PresetComboBox.ItemsSource = _overlayPresets.Select(p => p.Name).ToList();
		PresetComboBox.SelectedIndex = _overlayPresets.FindIndex(p => p.Id == _activePresetId);
		_suppressOverlayEvents = false;
	}

	private void RefreshElementCheckboxes()
	{
		List<OverlayElement> elements = ActiveElements;

		_suppressOverlayEvents = true;
		StatsBlockVisibleCheck.IsChecked = IsVisible(OverlayElementType.StatsBlock);
		CompassVisibleCheck.IsChecked = IsVisible(OverlayElementType.Compass);
		SunVisibleCheck.IsChecked = IsVisible(OverlayElementType.SunWidget);
		PitchVisibleCheck.IsChecked = IsVisible(OverlayElementType.PitchGauge);
		SpeedVisibleCheck.IsChecked = IsVisible(OverlayElementType.SpeedGauge);
		_suppressOverlayEvents = false;
		return;

		bool IsVisible(OverlayElementType type)
		{
			return elements.FirstOrDefault(el => el.Type == type)?.Visible ?? false;
		}
	}

	/// <summary>
	///     Swaps in a brand-new elements list rather than mutating the one already handed to
	///     PreviewPlayer - that list may be mid-enumeration on the playback thread's Render() call
	///     right now, and mutating it in place races with that enumeration.
	/// </summary>
	private void ReplaceActiveElements(List<OverlayElement> elements)
	{
		var index = _overlayPresets.FindIndex(p => p.Id == _activePresetId);
		if (index >= 0) _overlayPresets[index] = _overlayPresets[index] with { Elements = elements };
	}

	private void SetElementVisible(OverlayElementType type, bool visible)
	{
		if (_suppressOverlayEvents) return;

		List<OverlayElement> elements = ActiveElements.ToList();
		var index = elements.FindIndex(el => el.Type == type);
		if (index < 0) return;

		elements[index] = elements[index] with { Visible = visible };
		ReplaceActiveElements(elements);
		_previewPlayer.SetLayout(elements);
		SaveOverlayPresets();
	}

	private void OnStatsBlockVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.StatsBlock, StatsBlockVisibleCheck.IsChecked == true);
	}

	private void OnCompassVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.Compass, CompassVisibleCheck.IsChecked == true);
	}

	private void OnSunVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.SunWidget, SunVisibleCheck.IsChecked == true);
	}

	private void OnPitchVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.PitchGauge, PitchVisibleCheck.IsChecked == true);
	}

	private void OnSpeedVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.SpeedGauge, SpeedVisibleCheck.IsChecked == true);
	}

	private void OnPresetSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressOverlayEvents || PresetComboBox.SelectedIndex < 0) return;

		_activePresetId = _overlayPresets[PresetComboBox.SelectedIndex].Id;
		RefreshElementCheckboxes();
		_previewPlayer.SetLayout(ActiveElements);
		SaveOverlayPresets();
	}

	private void OnNewPresetClick(object? sender, RoutedEventArgs e)
	{
		if (_summary is null) return;

		var id = Guid.NewGuid().ToString("N");
		var preset = OverlayPreset.CreateDefault(id, $"Preset {_overlayPresets.Count + 1}",
			_summary.Video.Width, _summary.Video.Height);
		_overlayPresets.Add(preset);
		_activePresetId = id;

		RefreshPresetComboBox();
		RefreshElementCheckboxes();
		_previewPlayer.SetLayout(ActiveElements);
		SaveOverlayPresets();
	}

	private void OnDuplicatePresetClick(object? sender, RoutedEventArgs e)
	{
		OverlayPreset source = _overlayPresets.First(p => p.Id == _activePresetId);
		var id = Guid.NewGuid().ToString("N");
		var copy = new OverlayPreset(id, $"{source.Name} copy", source.Elements.Select(el => el).ToList());
		_overlayPresets.Add(copy);
		_activePresetId = id;

		RefreshPresetComboBox();
		RefreshElementCheckboxes();
		_previewPlayer.SetLayout(ActiveElements);
		SaveOverlayPresets();
	}

	private void OnDeletePresetClick(object? sender, RoutedEventArgs e)
	{
		if (_overlayPresets.Count <= 1)
		{
			AppendLog("Cannot delete the only remaining preset.");
			return;
		}

		_overlayPresets.RemoveAll(p => p.Id == _activePresetId);
		_activePresetId = _overlayPresets[0].Id;

		RefreshPresetComboBox();
		RefreshElementCheckboxes();
		_previewPlayer.SetLayout(ActiveElements);
		SaveOverlayPresets();
	}

	private void OnResetPresetClick(object? sender, RoutedEventArgs e)
	{
		if (_summary is null) return;

		var index = _overlayPresets.FindIndex(p => p.Id == _activePresetId);
		if (index < 0) return;

		_overlayPresets[index] = OverlayPreset.CreateDefault(_activePresetId, _overlayPresets[index].Name,
			_summary.Video.Width, _summary.Video.Height);

		RefreshElementCheckboxes();
		_previewPlayer.SetLayout(ActiveElements);
		SaveOverlayPresets();
	}

	private void OnRenamePresetClick(object? sender, RoutedEventArgs e)
	{
		if (_overlayPresets.Count == 0) return;

		PresetRenameBox.Text = _overlayPresets.First(p => p.Id == _activePresetId).Name;
		PresetComboBox.IsVisible = false;
		PresetRenameBox.IsVisible = true;
		PresetRenameBox.Focus();
		PresetRenameBox.SelectAll();
	}

	private void OnPresetRenameBoxKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter) CommitPresetRename();
		else if (e.Key == Key.Escape) CancelPresetRename();
	}

	private void OnPresetRenameBoxLostFocus(object? sender, RoutedEventArgs e)
	{
		if (PresetRenameBox.IsVisible) CommitPresetRename();
	}

	private void CommitPresetRename()
	{
		var newName = PresetRenameBox.Text?.Trim();
		if (!string.IsNullOrWhiteSpace(newName))
		{
			var index = _overlayPresets.FindIndex(p => p.Id == _activePresetId);
			if (index >= 0)
			{
				_overlayPresets[index] = _overlayPresets[index] with { Name = newName };
				SaveOverlayPresets();
			}
		}

		CancelPresetRename();
	}

	private void CancelPresetRename()
	{
		PresetRenameBox.IsVisible = false;
		PresetComboBox.IsVisible = true;
		RefreshPresetComboBox();
	}

	private Point? MapCanvasPointToFullRes(Point canvasPoint)
	{
		if (_summary is null || _previewBitmap is null) return null;

		var controlWidth = OverlayDragCanvas.Bounds.Width;
		var controlHeight = OverlayDragCanvas.Bounds.Height;
		var bitmapWidth = _previewBitmap.PixelSize.Width;
		var bitmapHeight = _previewBitmap.PixelSize.Height;
		if (controlWidth <= 0 || controlHeight <= 0 || bitmapWidth <= 0 || bitmapHeight <= 0) return null;

		// PreviewImage uses Stretch="Uniform", which letterboxes the bitmap inside the control -
		// replicate that math to turn a click on the control into a pixel in the preview bitmap.
		var scale = Math.Min(controlWidth / bitmapWidth, controlHeight / bitmapHeight);
		var renderedWidth = bitmapWidth * scale;
		var renderedHeight = bitmapHeight * scale;
		var offsetX = (controlWidth - renderedWidth) / 2;
		var offsetY = (controlHeight - renderedHeight) / 2;

		var localX = canvasPoint.X - offsetX;
		var localY = canvasPoint.Y - offsetY;
		if (localX < 0 || localY < 0 || localX > renderedWidth || localY > renderedHeight) return null;

		// The preview bitmap is a uniformly downscaled copy of the full render resolution.
		var fullResScale = _summary.Video.Width / (double)bitmapWidth;
		return new Point(localX / scale * fullResScale, localY / scale * fullResScale);
	}

	private void OnOverlayCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (_summary is null) return;
		if (MapCanvasPointToFullRes(e.GetPosition(OverlayDragCanvas)) is not { } pos) return;

		List<OverlayElement> elements = ActiveElements;
		for (var i = elements.Count - 1; i >= 0; i--)
		{
			OverlayElement el = elements[i];
			if (!el.Visible) continue;

			SKRect bounds = OverlayElementBounds.GetBounds(el.Type, el.X, el.Y);
			if (pos.X < bounds.Left || pos.X > bounds.Right || pos.Y < bounds.Top || pos.Y > bounds.Bottom) continue;

			// Dragging needs a stable frame to align against, and it eliminates a real race:
			// without pausing, the playback thread keeps calling Render() on the same elements
			// list this drag is about to replace concurrently.
			if (_previewPlayer.IsPlaying)
			{
				_previewPlayer.Pause();
				PlayPauseButton.Content = "Play";
			}

			_draggingElementType = el.Type;
			_dragAnchorOffset = new Point(pos.X - el.X, pos.Y - el.Y);
			e.Pointer.Capture(OverlayDragCanvas);
			return;
		}
	}

	private void OnOverlayCanvasPointerMoved(object? sender, PointerEventArgs e)
	{
		if (_draggingElementType is not { } type || _summary is null) return;
		if (MapCanvasPointToFullRes(e.GetPosition(OverlayDragCanvas)) is not { } pos) return;

		var newX = (float)Math.Clamp(pos.X - _dragAnchorOffset.X, 0, _summary.Video.Width);
		var newY = (float)Math.Clamp(pos.Y - _dragAnchorOffset.Y, 0, _summary.Video.Height);

		List<OverlayElement> elements = ActiveElements.ToList();
		var index = elements.FindIndex(el => el.Type == type);
		if (index < 0) return;

		elements[index] = elements[index] with { X = newX, Y = newY };
		ReplaceActiveElements(elements);
		_previewPlayer.SetLayout(elements);
	}

	private void OnOverlayCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		if (_draggingElementType is null) return;

		_draggingElementType = null;
		e.Pointer.Capture(null);
		SaveOverlayPresets();
	}

	protected override void OnClosed(EventArgs e)
	{
		_previewPlayer.Dispose();
		base.OnClosed(e);
	}

	private enum UiPhase
	{
		Idle,
		LoadingSummary,
		SummaryReady,
		Rendering
	}
}
