using System.Globalization;
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
using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using RenderOptions = OsmoOverlay.Core.RenderOptions;

namespace OsmoOverlay.Gui;

public partial class MainWindow : Window
{
	private const int PreviewMaxWidth = 960;

	// Simple check/cross tick marks (24x24 viewbox) drawn as vector geometry rather than a Unicode
	// glyph - a ✓/✗ character can silently fall back to a different font with its own baseline,
	// throwing off vertical alignment next to the surrounding text in a way that varies by system.
	private static readonly Geometry CheckGeometry = Geometry.Parse("M4.5 12.75l6 6 9-13.5");
	private static readonly Geometry CrossGeometry = Geometry.Parse("M6 18L18 6M6 6l12 12");

	private static readonly List<DateFormatOption> DateFormatOptions =
	[
		new("Default (dd/MM/yyyy HH:mm:ss)", null),
		new("yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd  HH:mm:ss"),
		new("MM/dd/yyyy hh:mm:ss tt", "MM/dd/yyyy  hh:mm:ss tt"),
		new("yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd  HH:mm:ss"),
		new("dd MMM yyyy HH:mm", "dd MMM yyyy  HH:mm"),
		new("HH:mm:ss", "HH:mm:ss")
	];

	private static readonly List<LocaleOption> LocaleOptions = BuildLocaleOptions();
	private readonly List<string> _inputPaths = [];
	private readonly PreviewPlayer _previewPlayer = new();
	private string _activePresetId = "";

	private CancellationTokenSource? _cts;
	private string _detectedEncoder = "";
	private Point _dragAnchorOffset;
	private OverlayElementType? _draggingElementType;
	private int? _frameLimit;

	private List<OverlayPreset> _overlayPresets = [];
	private UiPhase _phase = UiPhase.Idle;
	private WriteableBitmap? _previewBitmap;
	private bool _showWatermark = OverlaySettingsStore.Load().ShowWatermark;
	private bool _sliderDragInProgress;
	private FileSummary? _summary;
	private bool _suppressOverlayEvents;
	private bool _suppressSliderEvent;

	public MainWindow()
	{
		InitializeComponent();
		Opened += OnWindowOpened;

		DateTimeFormatCombo.ItemsSource = DateFormatOptions;
		DateTimeLocaleCombo.ItemsSource = LocaleOptions;

		_previewPlayer.FrameReady += OnPreviewFrameReady;
		_previewPlayer.PlaybackStopped += OnPreviewPlaybackStopped;
		_previewPlayer.Message += AppendLog;
	}

	// FirstOrDefault, not First: there's a narrow window right after picking a file where
	// _summary is already set but LoadOverlayPresets (an earlier await) hasn't finished yet, so a
	// pointer click on the drag canvas in that gap must not crash on an empty/stale preset list.
	private List<OverlayElement> ActiveElements =>
		_overlayPresets.FirstOrDefault(p => p.Id == _activePresetId)?.Elements ?? [];

	/// <summary>
	///     The built-in "Default" preset is read-only, so there's always one untouched baseline layout
	///     to fall back to or Duplicate from - editing it directly would mean losing that baseline the
	///     first time someone drags a widget.
	/// </summary>
	private bool IsActivePresetDefault => _activePresetId == OverlayPresetStore.DefaultPresetId;

	private async void OnWindowOpened(object? sender, EventArgs e)
	{
		Opened -= OnWindowOpened;

		IReadOnlyList<ExternalTool> missing = await DependencyChecker.FindMissingAsync(RequiredTools.All);
		if (missing.Count == 0) return;

		await new DependencyPromptWindow(missing).ShowDialog(this);
	}

	private async void OnAddInputFilesClick(object? sender, RoutedEventArgs e)
	{
		TopLevel? topLevel = GetTopLevel(this);
		if (topLevel is null) return;

		IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = "Select DJI Osmo Action recording(s) - pick several segments to stitch them together",
			AllowMultiple = true,
			FileTypeFilter = [new FilePickerFileType("MP4 video") { Patterns = ["*.mp4", "*.MP4"] }]
		});

		if (files.Count == 0) return;

		var wasEmpty = _inputPaths.Count == 0;
		_inputPaths.AddRange(files.Select(f => f.Path.LocalPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
		RefreshInputFilesList();

		if (wasEmpty)
			OutputPathBox.Text = RenderOptions.DefaultOutputPath(_inputPaths);

		ClosePreview();
		SetPhase(UiPhase.Idle);
		ActionButton.IsEnabled = _inputPaths.Count > 0;
	}

	private void OnMoveInputUpClick(object? sender, RoutedEventArgs e)
	{
		var index = InputFilesList.SelectedIndex;
		if (index <= 0) return;

		(_inputPaths[index - 1], _inputPaths[index]) = (_inputPaths[index], _inputPaths[index - 1]);
		RefreshInputFilesList();
		InputFilesList.SelectedIndex = index - 1;
	}

	private void OnMoveInputDownClick(object? sender, RoutedEventArgs e)
	{
		var index = InputFilesList.SelectedIndex;
		if (index < 0 || index >= _inputPaths.Count - 1) return;

		(_inputPaths[index + 1], _inputPaths[index]) = (_inputPaths[index], _inputPaths[index + 1]);
		RefreshInputFilesList();
		InputFilesList.SelectedIndex = index + 1;
	}

	private void OnRemoveInputClick(object? sender, RoutedEventArgs e)
	{
		var index = InputFilesList.SelectedIndex;
		if (index < 0) return;

		_inputPaths.RemoveAt(index);
		RefreshInputFilesList();

		ClosePreview();
		SetPhase(UiPhase.Idle);
		ActionButton.IsEnabled = _inputPaths.Count > 0;
	}

	private void RefreshInputFilesList()
	{
		InputFilesList.ItemsSource = _inputPaths.Select(Path.GetFileName).ToList();
	}

	private async void OnSettingsClick(object? sender, RoutedEventArgs e)
	{
		var settings = new SettingsWindow(_frameLimit, _showWatermark);
		await settings.ShowDialog(this);
		_frameLimit = settings.FrameLimit;

		if (settings.ShowWatermark != _showWatermark)
		{
			_showWatermark = settings.ShowWatermark;
			OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { ShowWatermark = _showWatermark });
			_previewPlayer.SetShowWatermark(_showWatermark);
		}

		if (_summary is not null && _phase == UiPhase.SummaryReady)
			PopulateOutputInfo(_summary, _detectedEncoder);
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
		if (_inputPaths.Count == 0 || _inputPaths.Any(p => !File.Exists(p)))
			return;

		ClosePreview();
		SetPhase(UiPhase.LoadingSummary);
		LogBox.Text = "";
		foreach (var path in _inputPaths)
			AppendLog($"Input: {path}");
		AppendLog("Probing source file(s) (ffprobe)...");
		AppendLog("Extracting telemetry (djmd stream)... falls back to exiftool if the raw layout doesn't match, unless cached.");

		try
		{
			FileSummary summary = await Task.Run(() => FileSummaryReader.Read(_inputPaths));

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
				await OpenPreviewAsync(summary);
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
		var outputPath = OutputPathBox.Text ?? "";

		if (string.IsNullOrWhiteSpace(outputPath))
		{
			AppendLog("Enter an output file path.");
			return;
		}

		var frameLimit = _frameLimit;

		_cts = new CancellationTokenSource();
		SetPhase(UiPhase.Rendering);
		LogBox.Text = "";
		Progress.Value = 0;
		OutMeasuredPanel.IsVisible = false;
		OutPlanText.IsVisible = true;
		OutFrames.IsVisible = true;

		var progress = new Progress<RenderStatus>(OnProgress);
		IReadOnlyList<OverlayElement>? layout = _overlayPresets.Count > 0 ? ActiveElements : null;
		var options = new RenderOptions(_inputPaths, outputPath, frameLimit, _detectedEncoder, _summary?.TelemetryFrames,
			Layout: layout, ShowWatermark: _showWatermark);

		AppendLog($"Output: {outputPath}");
		AppendLog($"Encoder: {_detectedEncoder}, frame limit: {(frameLimit is { } fl ? fl.ToString() : "none")}");
		var presetName = _overlayPresets.FirstOrDefault(p => p.Id == _activePresetId)?.Name;
		AppendLog($"Overlay preset: {presetName ?? "default (none loaded)"}");
		AppendLog($"CLI equivalent: {BuildCliCommand(outputPath, frameLimit)}");

		RenderResult result;
		try
		{
			result = await RenderJob.RunAsync(options, progress, _cts.Token);
		}
		finally
		{
			_cts = null;
		}

		if (result.Success)
		{
			Progress.Value = 100;
			var sizeText = File.Exists(outputPath) ? $", {FormatBytes(new FileInfo(outputPath).Length)}" : "";
			AppendLog($"Done: {outputPath} (time: {result.Elapsed:hh\\:mm\\:ss}{sizeText})");

			if (_summary is not null) PopulateMeasuredOutputInfo(outputPath, _summary);
		}
		else
		{
			AppendLog($"Error: {result.ErrorMessage}");
		}

		SetPhase(UiPhase.SummaryReady);
		ActionButton.IsEnabled = true;
	}

	private string BuildCliCommand(string outputPath, int? frameLimit)
	{
		var parts = new List<string> { "OsmoOverlay.Cli" };
		parts.AddRange(_inputPaths.Select(QuoteArg));
		parts.Add("-o");
		parts.Add(QuoteArg(outputPath));
		if (frameLimit is { } limit)
		{
			parts.Add("--frames");
			parts.Add(limit.ToString());
		}

		return string.Join(' ', parts);
	}

	private static string QuoteArg(string value)
	{
		return value.Contains(' ') ? $"\"{value}\"" : value;
	}

	private void OnCancelClick(object? sender, RoutedEventArgs e)
	{
		_cts?.Cancel();
	}

	private void SetPhase(UiPhase phase)
	{
		_phase = phase;

		var summaryVisible = phase is UiPhase.SummaryReady or UiPhase.Rendering;
		SummaryPanel.IsVisible = summaryVisible;
		OutputInfoCard.IsVisible = summaryVisible;
		FileInfoCard.IsVisible = phase == UiPhase.SummaryReady;
		TelemetryCard.IsVisible = phase == UiPhase.SummaryReady && _summary?.Telemetry is not null;
		var overlayReady = summaryVisible && _summary?.HasTelemetry == true;
		OverlayContent.IsVisible = overlayReady;
		OverlayPlaceholder.IsVisible = !overlayReady;

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

		RecommendedSettings? recommended = RecommendedSettings.ForCameraModel(summary.CameraModel);

		bool? Check(Func<RecommendedSettings, bool> predicate)
		{
			return recommended is { } r ? predicate(r) : null;
		}

		InfoResolution.Text = $"{summary.Video.Width}x{summary.Video.Height}";
		SetCheck(InfoResolutionCheck, Check(r => summary.Video.Width >= r.Width && summary.Video.Height >= r.Height),
			recommended is { } r1 ? $"{r1.Width}x{r1.Height} or higher" : null);

		InfoFrameRate.Text = $"{FormatFps(summary.Video.Fps)} fps";
		SetCheck(InfoFrameRateCheck, Check(r => Math.Abs(summary.Video.Fps - r.Fps) < 0.5),
			recommended is { } r2 ? $"~{r2.Fps:0.##} fps" : null);

		InfoCodec.Text = string.IsNullOrEmpty(summary.Video.Profile)
			? summary.Video.CodecName
			: $"{summary.Video.CodecName} ({summary.Video.Profile})";
		SetCheck(InfoCodecCheck,
			Check(_ => summary.Video.CodecName.Equals("hevc", StringComparison.OrdinalIgnoreCase) &&
			           summary.Video.Profile.Contains("10", StringComparison.OrdinalIgnoreCase)),
			"HEVC (H.265), 10-bit");

		InfoPixFmt.Text = summary.Video.PixFmt;
		SetCheck(InfoPixFmtCheck, Check(_ => summary.Video.PixFmt.Contains("10le", StringComparison.OrdinalIgnoreCase)),
			"10-bit (yuv420p10le)");

		var primaries = summary.Video.ColorPrimaries ?? "?";
		var transfer = summary.Video.ColorTransfer ?? "?";
		var colorSpace = summary.Video.ColorSpace ?? "?";
		var range = summary.Video.ColorRange ?? "?";
		InfoColor.Text = primaries == transfer && transfer == colorSpace
			? $"{primaries} ({range})"
			: $"{primaries} / {transfer} / {colorSpace} ({range})";
		SetCheck(InfoColorCheck,
			Check(_ => primaries == "bt709" && transfer == "bt709" && colorSpace == "bt709" && range == "tv"),
			"bt709 / bt709 / bt709 (tv range)");

		InfoBitrate.Text = $"{summary.Video.BitRate / 1_000_000.0:0.#} Mbps";
		SetCheck(InfoBitrateCheck, Check(r => summary.Video.BitRate >= r.MinVideoBitrate),
			recommended is { } r3 ? $"at least {r3.MinVideoBitrate / 1_000_000.0:0.#} Mbps" : null);

		InfoDuration.Text = TimeSpan.FromSeconds(summary.DurationSeconds).ToString(@"hh\:mm\:ss");
		InfoFileSize.Text = FormatBytes(summary.FileSizeBytes);

		AudioGrid.IsVisible = summary.Audio is not null;
		InfoAudioNone.IsVisible = summary.Audio is null;
		if (summary.Audio is { } a)
		{
			InfoAudioCodec.Text = a.CodecName;
			InfoAudioSampleRate.Text = $"{a.SampleRate} Hz";
			InfoAudioChannels.Text = $"{a.Channels}ch";
			InfoAudioBitrate.Text = $"{a.BitRate / 1000.0:0} kbps";
			SetCheck(InfoAudioBitrateCheck, Check(r => a.BitRate >= r.MinAudioBitrate),
				recommended is { } r4 ? $"at least {r4.MinAudioBitrate / 1000.0:0} kbps" : null);
		}

		InfoTelemetry.Text = summary.HasTelemetry ? "Detected" : "Not found";
		InfoTelemetry.Foreground = summary.HasTelemetry
			? new SolidColorBrush(Color.Parse("#4CAF50"))
			: new SolidColorBrush(Color.Parse("#E5484D"));
		TelemetryPill.Background = summary.HasTelemetry
			? new SolidColorBrush(Color.Parse("#3D4CAF50"))
			: new SolidColorBrush(Color.Parse("#3DE5484D"));
	}

	/// <summary>
	///     ok is null when no RecommendedSettings entry exists for the detected camera model - in
	///     that case no icon is shown at all rather than judging against a mismatched reference.
	///     recommendedDescription is the human-readable recommended value (e.g. "at least 70 Mbps"),
	///     used to phrase the tooltip depending on whether this field actually matches it.
	/// </summary>
	private static void SetCheck(AvaloniaPath path, bool? ok, string? recommendedDescription)
	{
		path.Data = ok switch { true => CheckGeometry, false => CrossGeometry, null => null };
		path.Stroke = ok switch
		{
			true => new SolidColorBrush(Color.Parse("#4CAF50")),
			false => new SolidColorBrush(Color.Parse("#E5484D")),
			null => null
		};
		ToolTip.SetTip(path, ok switch
		{
			true => $"Nice - this is the recommended setting for your camera ({recommendedDescription}).",
			false => $"Not the recommended setting for your camera - recommended: {recommendedDescription}.",
			null => null
		});
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
		var frameLimit = _frameLimit;
		var totalFrames = frameLimit ?? (int)Math.Ceiling(summary.DurationSeconds * fps);
		var outDurationSeconds = frameLimit is not null ? frameLimit.Value / fps : summary.DurationSeconds;

		OutEncoder.Text = encoder + (encoder == "libx265" ? " (CPU)" : " (GPU)");
		OutAudio.Text = summary.Audio is not null ? "Copied (no re-encode)" : "None";
		OutFrames.Text = $"{totalFrames} frames (~{TimeSpan.FromSeconds(outDurationSeconds):hh\\:mm\\:ss})";

		// A changed setting (frame limit, input file) invalidates whatever was measured from a
		// previous export, so fall back to the plan until the next render actually produces a file.
		OutPlanText.IsVisible = true;
		OutFrames.IsVisible = true;
		OutMeasuredPanel.IsVisible = false;
	}

	/// <summary>
	///     Replaces the pre-render plan with what ffprobe actually measured from the exported file, so
	///     "matches the source" is a verified fact rather than an assumption baked into the UI text.
	/// </summary>
	private void PopulateMeasuredOutputInfo(string outputPath, FileSummary inputSummary)
	{
		SourceInfo output;
		try
		{
			output = SourceProbe.Probe(outputPath);
		}
		catch (Exception ex)
		{
			AppendLog($"Could not verify the exported file: {ex.Message}");
			return;
		}

		OutPlanText.IsVisible = false;
		OutFrames.IsVisible = false;
		OutMeasuredPanel.IsVisible = true;

		OutResolution.Text = $"{output.Video.Width}x{output.Video.Height}";
		SetMatchCheck(OutResolutionCheck,
			output.Video.Width == inputSummary.Video.Width && output.Video.Height == inputSummary.Video.Height);

		OutFrameRate.Text = $"{FormatFps(output.Video.Fps)} fps";
		SetMatchCheck(OutFrameRateCheck, Math.Abs(output.Video.Fps - inputSummary.Video.Fps) < 0.01);

		OutCodec.Text = string.IsNullOrEmpty(output.Video.Profile)
			? output.Video.CodecName
			: $"{output.Video.CodecName} ({output.Video.Profile})";
		SetMatchCheck(OutCodecCheck,
			output.Video.CodecName.Equals(inputSummary.Video.CodecName, StringComparison.OrdinalIgnoreCase) &&
			output.Video.Profile.Equals(inputSummary.Video.Profile, StringComparison.OrdinalIgnoreCase));

		OutPixFmt.Text = output.Video.PixFmt;
		SetMatchCheck(OutPixFmtCheck,
			output.Video.PixFmt.Equals(inputSummary.Video.PixFmt, StringComparison.OrdinalIgnoreCase));

		var primaries = output.Video.ColorPrimaries ?? "?";
		var transfer = output.Video.ColorTransfer ?? "?";
		var colorSpace = output.Video.ColorSpace ?? "?";
		var range = output.Video.ColorRange ?? "?";
		OutColor.Text = primaries == transfer && transfer == colorSpace
			? $"{primaries} ({range})"
			: $"{primaries} / {transfer} / {colorSpace} ({range})";
		SetMatchCheck(OutColorCheck,
			primaries == (inputSummary.Video.ColorPrimaries ?? "?") &&
			transfer == (inputSummary.Video.ColorTransfer ?? "?") &&
			colorSpace == (inputSummary.Video.ColorSpace ?? "?") &&
			range == (inputSummary.Video.ColorRange ?? "?"));

		OutBitrate.Text = $"{output.Video.BitRate / 1_000_000.0:0.#} Mbps";
		// VBR naturally drifts from the source's own bitrate - "matches" means close, not byte-exact.
		var bitrateRatio = (double)output.Video.BitRate / inputSummary.Video.BitRate;
		SetMatchCheck(OutBitrateCheck, bitrateRatio is >= 0.7 and <= 1.5);

		OutMeasuredDuration.Text = TimeSpan.FromSeconds(output.DurationSeconds).ToString(@"hh\:mm\:ss");
		OutMeasuredFileSize.Text = FormatBytes(new FileInfo(outputPath).Length);
	}

	private static void SetMatchCheck(AvaloniaPath path, bool matches)
	{
		path.Data = matches ? CheckGeometry : CrossGeometry;
		path.Stroke = new SolidColorBrush(Color.Parse(matches ? "#4CAF50" : "#E5484D"));
		ToolTip.SetTip(path, matches ? "Matches the source file." : "Differs from the source file.");
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

	private async Task OpenPreviewAsync(FileSummary summary)
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

			await _previewPlayer.OpenAsync(summary, previewWidth, previewHeight);
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
		DateTimeVisibleCheck.IsChecked = IsVisible(OverlayElementType.DateTimeText);
		ElevationVisibleCheck.IsChecked = IsVisible(OverlayElementType.Elevation);
		GradientVisibleCheck.IsChecked = IsVisible(OverlayElementType.Gradient);
		DistanceVisibleCheck.IsChecked = IsVisible(OverlayElementType.Distance);
		CompassVisibleCheck.IsChecked = IsVisible(OverlayElementType.Compass);
		SunVisibleCheck.IsChecked = IsVisible(OverlayElementType.SunWidget);
		PitchVisibleCheck.IsChecked = IsVisible(OverlayElementType.PitchGauge);
		SpeedVisibleCheck.IsChecked = IsVisible(OverlayElementType.SpeedGauge);
		MapVisibleCheck.IsChecked = IsVisible(OverlayElementType.MapWidget);

		OverlayElement? map = Find(OverlayElementType.MapWidget);
		MapTileUrlBox.Text = map?.MapTileUrlTemplate;
		MapZoomBox.Value = map?.MapZoom ?? 16;
		MapAttributionBox.Text = map?.MapAttribution;

		OverlayElement? dateTime = Find(OverlayElementType.DateTimeText);
		DateTimeFormatCombo.SelectedItem =
			DateFormatOptions.FirstOrDefault(o => o.Format == dateTime?.DateFormat) ?? DateFormatOptions[0];
		DateTimeLocaleCombo.SelectedItem =
			LocaleOptions.FirstOrDefault(o => o.CultureName == dateTime?.Locale) ?? LocaleOptions[0];

		OverlayElement? elevation = Find(OverlayElementType.Elevation);
		ElevationLabelBox.Text = elevation?.Label;
		SetUnitsRadio(ElevationMetricRadio, ElevationImperialRadio, elevation?.Units ?? UnitSystem.Metric);

		GradientLabelBox.Text = Find(OverlayElementType.Gradient)?.Label;

		OverlayElement? distance = Find(OverlayElementType.Distance);
		DistanceLabelBox.Text = distance?.Label;
		SetUnitsRadio(DistanceMetricRadio, DistanceImperialRadio, distance?.Units ?? UnitSystem.Metric);

		SetUnitsRadio(SpeedMetricRadio, SpeedImperialRadio, Find(OverlayElementType.SpeedGauge)?.Units ?? UnitSystem.Metric);

		var editable = !IsActivePresetDefault;
		RenamePresetButton.IsEnabled = editable;
		DeletePresetButton.IsEnabled = editable;
		ResetPresetButton.IsEnabled = editable;
		DefaultPresetLockedHint.IsVisible = !editable;

		DateTimeVisibleCheck.IsEnabled = editable;
		ElevationVisibleCheck.IsEnabled = editable;
		GradientVisibleCheck.IsEnabled = editable;
		DistanceVisibleCheck.IsEnabled = editable;
		CompassVisibleCheck.IsEnabled = editable;
		SunVisibleCheck.IsEnabled = editable;
		PitchVisibleCheck.IsEnabled = editable;
		SpeedVisibleCheck.IsEnabled = editable;
		MapVisibleCheck.IsEnabled = editable;
		DateTimeGearButton.IsEnabled = editable;
		ElevationGearButton.IsEnabled = editable;
		GradientGearButton.IsEnabled = editable;
		DistanceGearButton.IsEnabled = editable;
		SpeedGearButton.IsEnabled = editable;
		MapGearButton.IsEnabled = editable;

		_suppressOverlayEvents = false;
		return;

		bool IsVisible(OverlayElementType type)
		{
			return elements.FirstOrDefault(el => el.Type == type)?.Visible ?? false;
		}

		OverlayElement? Find(OverlayElementType type)
		{
			return elements.FirstOrDefault(el => el.Type == type);
		}

		static void SetUnitsRadio(RadioButton metric, RadioButton imperial, UnitSystem units)
		{
			metric.IsChecked = units == UnitSystem.Metric;
			imperial.IsChecked = units == UnitSystem.Imperial;
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

	private void UpdateElement(OverlayElementType type, Func<OverlayElement, OverlayElement> update)
	{
		if (_suppressOverlayEvents || IsActivePresetDefault) return;

		List<OverlayElement> elements = [.. ActiveElements];
		var index = elements.FindIndex(el => el.Type == type);
		if (index < 0) return;

		elements[index] = update(elements[index]);
		ReplaceActiveElements(elements);
		_previewPlayer.SetLayout(elements);
		SaveOverlayPresets();
	}

	private void SetElementVisible(OverlayElementType type, bool visible)
	{
		UpdateElement(type, el => el with { Visible = visible });
	}

	private void SetElementUnits(OverlayElementType type, UnitSystem units)
	{
		UpdateElement(type, el => el with { Units = units });
	}

	private void SetElementLabel(OverlayElementType type, string? label)
	{
		var trimmed = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
		UpdateElement(type, el => el with { Label = trimmed });
	}

	private void SetElementDateFormat(OverlayElementType type, string? format)
	{
		UpdateElement(type, el => el with { DateFormat = format });
	}

	private void SetElementLocale(OverlayElementType type, string? locale)
	{
		UpdateElement(type, el => el with { Locale = locale });
	}

	private void OnDateTimeVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.DateTimeText, DateTimeVisibleCheck.IsChecked == true);
	}

	private void OnElevationVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.Elevation, ElevationVisibleCheck.IsChecked == true);
	}

	private void OnGradientVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.Gradient, GradientVisibleCheck.IsChecked == true);
	}

	private void OnDistanceVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.Distance, DistanceVisibleCheck.IsChecked == true);
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

	private void OnDateTimeFormatChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressOverlayEvents || DateTimeFormatCombo.SelectedItem is not DateFormatOption option) return;
		SetElementDateFormat(OverlayElementType.DateTimeText, option.Format);
	}

	private void OnDateTimeLocaleChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressOverlayEvents || DateTimeLocaleCombo.SelectedItem is not LocaleOption option) return;
		SetElementLocale(OverlayElementType.DateTimeText, option.CultureName);
	}

	private void OnElevationLabelChanged(object? sender, RoutedEventArgs e)
	{
		SetElementLabel(OverlayElementType.Elevation, ElevationLabelBox.Text);
	}

	private void OnElevationUnitsChanged(object? sender, RoutedEventArgs e)
	{
		SetElementUnits(OverlayElementType.Elevation, ElevationImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric);
	}

	private void OnGradientLabelChanged(object? sender, RoutedEventArgs e)
	{
		SetElementLabel(OverlayElementType.Gradient, GradientLabelBox.Text);
	}

	private void OnDistanceLabelChanged(object? sender, RoutedEventArgs e)
	{
		SetElementLabel(OverlayElementType.Distance, DistanceLabelBox.Text);
	}

	private void OnDistanceUnitsChanged(object? sender, RoutedEventArgs e)
	{
		SetElementUnits(OverlayElementType.Distance, DistanceImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric);
	}

	private void OnSpeedUnitsChanged(object? sender, RoutedEventArgs e)
	{
		SetElementUnits(OverlayElementType.SpeedGauge, SpeedImperialRadio.IsChecked == true ? UnitSystem.Imperial : UnitSystem.Metric);
	}

	private void OnMapVisibilityChanged(object? sender, RoutedEventArgs e)
	{
		SetElementVisible(OverlayElementType.MapWidget, MapVisibleCheck.IsChecked == true);
	}

	private void OnMapTileUrlChanged(object? sender, RoutedEventArgs e)
	{
		var url = MapTileUrlBox.Text;
		UpdateElement(OverlayElementType.MapWidget,
			el => el with { MapTileUrlTemplate = string.IsNullOrWhiteSpace(url) ? null : url.Trim() });
	}

	private void OnMapZoomChanged(object? sender, NumericUpDownValueChangedEventArgs e)
	{
		if (_suppressOverlayEvents || MapZoomBox.Value is not { } zoom) return;
		UpdateElement(OverlayElementType.MapWidget, el => el with { MapZoom = (int)zoom });
	}

	private void OnMapAttributionChanged(object? sender, RoutedEventArgs e)
	{
		var text = MapAttributionBox.Text;
		UpdateElement(OverlayElementType.MapWidget,
			el => el with { MapAttribution = string.IsNullOrWhiteSpace(text) ? null : text.Trim() });
	}

	/// <summary>
	///     Pulls the language list from .NET's own culture database instead of hand-maintaining one, so
	///     it covers whatever locales the runtime supports without the GUI needing to keep up.
	/// </summary>
	private static List<LocaleOption> BuildLocaleOptions()
	{
		List<LocaleOption> options = [new("System default", null)];
		options.AddRange(CultureInfo.GetCultures(CultureTypes.SpecificCultures)
			.OrderBy(c => c.NativeName, StringComparer.Ordinal)
			.Select(c => new LocaleOption($"{c.NativeName} ({c.Name})", c.Name)));
		return options;
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
		var copy = new OverlayPreset(id, $"{source.Name} copy", [.. source.Elements]);
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
		if (_summary is null || IsActivePresetDefault) return;
		if (MapCanvasPointToFullRes(e.GetPosition(OverlayDragCanvas)) is not { } pos) return;

		var scale = OverlayElementBounds.GetScale(_summary.Video.Width, _summary.Video.Height);
		List<OverlayElement> elements = ActiveElements;
		for (var i = elements.Count - 1; i >= 0; i--)
		{
			OverlayElement el = elements[i];
			if (!el.Visible) continue;

			SKRect bounds = OverlayElementBounds.GetBounds(el.Type, el.X, el.Y, scale);
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
			OverlayDragCanvas.Cursor = new Cursor(StandardCursorType.SizeAll);
			return;
		}
	}

	private void OnOverlayCanvasPointerMoved(object? sender, PointerEventArgs e)
	{
		if (_draggingElementType is not { } type || _summary is null) return;
		if (MapCanvasPointToFullRes(e.GetPosition(OverlayDragCanvas)) is not { } pos) return;

		var newX = (float)Math.Clamp(pos.X - _dragAnchorOffset.X, 0, _summary.Video.Width);
		var newY = (float)Math.Clamp(pos.Y - _dragAnchorOffset.Y, 0, _summary.Video.Height);

		List<OverlayElement> elements = [.. ActiveElements];
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
		OverlayDragCanvas.Cursor = new Cursor(StandardCursorType.Hand);
		SaveOverlayPresets();
	}

	protected override void OnClosed(EventArgs e)
	{
		_previewPlayer.Dispose();
		base.OnClosed(e);
	}

	private sealed record DateFormatOption(string Display, string? Format)
	{
		public override string ToString()
		{
			return Display;
		}
	}

	private sealed record LocaleOption(string Display, string? CultureName)
	{
		public override string ToString()
		{
			return Display;
		}
	}

	private enum UiPhase
	{
		Idle,
		LoadingSummary,
		SummaryReady,
		Rendering
	}
}
