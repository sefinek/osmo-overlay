using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Gui.Native;

namespace OsmoOverlay.Gui;

/// <summary>"Get Summary" (probing/telemetry extraction) and populating the Input/Telemetry/Output info cards from the resulting FileSummary.</summary>
public partial class MainWindow
{
	// Simple check/cross tick marks (24x24 viewbox) drawn as vector geometry rather than a Unicode
	// glyph - a ✓/✗ character can silently fall back to a different font with its own baseline,
	// throwing off vertical alignment next to the surrounding text in a way that varies by system.

	private async Task RunGetSummaryAsync()
	{
		if (_inputPaths.Count == 0 || _inputPaths.Any(p => !File.Exists(p)))
			return;

		List<string> inputPaths = [.. _inputPaths];

		ClosePreview();
		SetPhase(UiPhase.LoadingSummary);
		_hasGpsFix = false;
		// Cuts belong to the recording they were set on.
		ClearCuts();
		_hasGpsTimestamp = false;
		_hasContainerTime = false;
		LogBox.ClearLog();
		// Indeterminate rather than a percentage - probing/extraction/preview-open (which may itself
		// fetch map tiles) has no single reliable "done fraction" to report, unlike the render below.
		TaskbarProgress.SetState(this, TaskbarProgress.State.Indeterminate);
		foreach (var path in inputPaths)
			AppendLog($"Input: {path}");

		// Cache events arrive on the worker thread, mid-Read - posted through the same UI context the
		// await below resumes on, so they land in the log in order and before the results.
		SynchronizationContext? ui = SynchronizationContext.Current;

		try
		{
			FileSummary summary = await Task.Run(() => FileSummaryReader.Read(inputPaths,
				cacheEvent => ui?.Post(_ => LogCacheEvent(cacheEvent), null)));

			AppendLog(
				$"ffprobe: {summary.Video.CodecName} {summary.Video.Profile}, {summary.Video.Width}x{summary.Video.Height}, " +
				$"{FormatFps(summary.Video.Fps)} fps, {summary.Video.PixFmt}, ~{summary.Video.BitRate / 1_000_000} Mbps");
			AppendLog(
				$"Color: {summary.Video.ColorPrimaries ?? "?"} / {summary.Video.ColorTransfer ?? "?"} / " +
				$"{summary.Video.ColorSpace ?? "?"} ({summary.Video.ColorRange ?? "?"})");
			AppendLog($"Duration: {TimeSpan.FromSeconds(summary.DurationSeconds):hh\\:mm\\:ss}, size: {FormatHelper.FormatBytes(summary.FileSizeBytes)}");
			AppendLog(summary.Audio is { } audio
				? $"Audio: {audio.CodecName}, {audio.SampleRate} Hz, {audio.Channels}ch"
				: "Audio: none");

			AppendLog($"Camera model: {summary.CameraModel ?? "unknown"}");
			if (summary.Telemetry is { } t)
				AppendLog($"Telemetry stream detected (djmd) - {summary.TelemetryFrames?.Count ?? 0} raw samples, " +
				          $"{summary.DerivedFrames?.Count ?? 0} derived frames");
			else
				AppendLog("No telemetry stream found - this file cannot be rendered", LogLevel.Error);

			_hasContainerTime = summary.ContainerRecordingStartUtc is not null;

			if (summary.TelemetryFrames is { Count: > 0 } rawFrames)
			{
				_hasGpsFix = TelemetryProcessor.HasAnyGpsFix(rawFrames);
				_hasGpsTimestamp = TelemetryProcessor.HasAnyGpsTimestamp(rawFrames);

				var withGpsSpeed = rawFrames.Count(f => f.GpsSpeedMs is not null);
				AppendLog(withGpsSpeed > 0
					? $"GPS-measured speed: {withGpsSpeed}/{rawFrames.Count} frames (protobuf djmd velocity); " +
					  $"{rawFrames.Count - withGpsSpeed} fall back to derived speed"
					: "GPS-measured speed: not available for this file - using derived speed for all frames");

				if (!_hasGpsFix)
				{
					// No fix anywhere isn't an "anomaly" to flag on the preview timeline - it's just this
					// recording's normal state (e.g. filmed indoors) - see OpenPreviewAsync.
					// Still a real limitation worth flagging in the log (Warn), same amber as the GUI's
					// own "No GPS fix" pill/tooltip elsewhere.
					AppendLog("GPS: not present in this recording - position-based widgets (Compass, Map, " +
					          "Elevation, Gradient, Distance, Speed) are unavailable and greyed out", LogLevel.Warn);
				}
				else
				{
					List<(double Start, double End)> gpsLossRanges = TelemetryProcessor.FindGpsLossRanges(rawFrames);
					if (gpsLossRanges.Count > 0)
						AppendLog($"GPS signal lost: {gpsLossRanges.Count} range(s), {gpsLossRanges.Sum(r => r.End - r.Start):0.0}s total " +
						          "(shown as amber marks under the preview timeline)", LogLevel.Warn);
					else
						AppendLog("GPS signal: no loss detected");
				}

				if (!_hasGpsTimestamp)
					AppendLog(_hasContainerTime
						? "GPS timestamp: not present in this recording - Date & time / UTC time fall back to the " +
						  "file's own recording-start time instead (approximate, not GPS-synced; flagged with ⚠ in the widget list)"
						: "GPS timestamp: not present in this recording, and no usable recording-start time either - " +
						  "Date & time / UTC time are unavailable and greyed out", LogLevel.Warn);

				var withCameraSettings = rawFrames.Count(f => f.Iso is not null);
				AppendLog(withCameraSettings == rawFrames.Count
					? "Telemetry source: native djmd decoder (ISO/shutter/color temp all present, exiftool not needed)"
					: withCameraSettings > 0
						? $"Telemetry source: native djmd decoder, partial camera settings ({withCameraSettings}/{rawFrames.Count})"
						: "Telemetry source: exiftool fallback (native djmd decode failed or ISO/shutter/CT unavailable)");
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
			GreenScreenButton.IsEnabled = summary.HasTelemetry;

			if (summary.HasTelemetry)
				await OpenPreviewAsync(summary);
		}
		catch (Exception ex)
		{
			AppendLog($"Could not read file info: {ex.Message}", LogLevel.Error);
			SetPhase(UiPhase.Idle);
			ActionButton.IsEnabled = true;
		}
		finally
		{
			TaskbarProgress.SetState(this, TaskbarProgress.State.NoProgress);
		}
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
		InfoFileSize.Text = FormatHelper.FormatBytes(summary.FileSizeBytes);

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

		// "Detected" alone would read as "full telemetry" even for a recording that never had a GPS
		// fix (accelerometer/camera-settings data only, decoded from the same djmd stream) - a
		// distinct amber "No GPS fix" state instead of lumping it in with the green case, so it isn't
		// mistaken for a recording with real position data.
		var hasGpsFix = summary.TelemetryFrames is { Count: > 0 } telemetryFrames && TelemetryProcessor.HasAnyGpsFix(telemetryFrames);
		IBrush telemetryBrush = !summary.HasTelemetry ? Palette.Danger : hasGpsFix ? Palette.Success : Palette.Warning;
		InfoTelemetry.Text = !summary.HasTelemetry ? "Not found" : hasGpsFix ? "Detected" : "No GPS fix";
		InfoTelemetry.Foreground = telemetryBrush;
		TelemetryPill.Background = Palette.Tint(telemetryBrush, 0.24);
		ToolTip.SetTip(TelemetryPill, summary.HasTelemetry && !hasGpsFix
			? "This recording never acquired a GPS fix - only accelerometer/camera-settings data was decoded. Speed, distance, elevation, map and compass are unavailable."
			: null);
	}

	/// <summary>
	///     ok is null when no RecommendedSettings entry exists for the detected camera model - in
	///     that case no icon is shown at all rather than judging against a mismatched reference.
	///     recommendedDescription is the human-readable recommended value (e.g. "at least 70 Mbps"),
	///     used to phrase the tooltip depending on whether this field actually matches it.
	/// </summary>
	private static void SetCheck(IconView icon, bool? ok, string? recommendedDescription)
	{
		icon.Data = ok switch { true => Icons.Check, false => Icons.Close, null => null };
		icon.Foreground = ok switch
		{
			true => Palette.Success,
			false => Palette.Danger,
			null => null
		};
		ToolTip.SetTip(StatusRow(icon), ok switch
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

		// A flat "0.00 km" / "0 - 0 m" here reads the same whether the recording genuinely never
		// moved/climbed or - as for a file with no GPS fix at all - the position data needed to
		// compute them simply doesn't exist. Same tooltip signal as InfoTelemetry's "No GPS fix" pill.
		var noGpsFixTip = _hasGpsFix ? null : "No GPS fix in this recording - this reads 0, not a real measurement.";
		ToolTip.SetTip(TeleDistance, noGpsFixTip);
		ToolTip.SetTip(TeleMaxSpeed, noGpsFixTip);
		ToolTip.SetTip(TeleAltitude, noGpsFixTip);
	}

	private void LogCacheEvent(FileSummaryCacheEvent e)
	{
		switch (e.Kind)
		{
			case FileSummaryCacheEventKind.Hit:
				AppendLog($"Cache hit (format v{e.CurrentFormatVersion}): using the cached analysis, file unchanged since last run.");
				return;
			case FileSummaryCacheEventKind.Stale:
				AppendLog($"Cache outdated: found format v{e.PreviousFormatVersion}, current is v{e.CurrentFormatVersion} " +
				          "(telemetry logic changed since). Old cache entry deleted - generating a new one...", LogLevel.Warn);
				break;
			case FileSummaryCacheEventKind.Miss:
				AppendLog("No cache for this file yet (or the file changed since) - analyzing...");
				break;
			case FileSummaryCacheEventKind.Saved:
				AppendLog($"New cache saved (format v{e.CurrentFormatVersion}).");
				return;
			case FileSummaryCacheEventKind.SaveFailed:
				AppendLog("Could not save the cache - see the log; the next run will analyze this file again.", LogLevel.Warn);
				return;
		}

		AppendLog("Probing source file(s) (ffprobe)...");
		AppendLog("Extracting telemetry (djmd stream)... falls back to exiftool if the raw layout doesn't match.");
	}

	private void PopulateOutputInfo(FileSummary summary, string encoder)
	{
		var fps = summary.Video.Fps;
		var totalFrames = PlannedFrameCount();

		OutEncoder.Text = encoder + (encoder == "libx265" ? " (CPU)" : " (GPU)");
		// Same condition FfmpegPipeline uses: pieces joined by the concat filter can't have their audio stream-copied.
		OutAudio.Text = summary.Audio is null ? "None"
			: _outputTimeline?.Plan.Pieces.Count > 1 ? "AAC at the source bitrate (re-encoded to join the cuts)"
			: "Copied (no re-encode)";
		OutFrameCount.Text = totalFrames.ToString("N0", CultureInfo.CurrentCulture) + (HasCuts ? $" ({DescribeCuts()})" : "");

		// Speed measured by the last full render of this same shape (RenderSpeedHistory) - there's no
		// meaningful way to predict it before the first one, so say so instead of guessing.
		OverlaySettings settings = OverlaySettingsStore.Load();
		OutPlanText.Text = DescribeEncodePlan(settings, encoder);
		var key = RenderSpeedHistory.Key(summary.Video.Width, summary.Video.Height, fps, encoder, settings.NvencPreset);
		if (RenderSpeedHistory.TryGet(key) is { } renderFps)
		{
			OutEstimatedTime.Text = $"~{TimeSpan.FromSeconds(totalFrames / renderFps):hh\\:mm\\:ss}";
			ToolTip.SetTip(OutEstimatedTime,
				$"Based on your last render at this resolution/frame rate with {encoder}: {renderFps:0.#} fps " +
				$"({renderFps / fps:0.00}x realtime). Map tiles, settings or the footage itself can shift it a bit.");
		}
		else
		{
			OutEstimatedTime.Text = "known after the first render";
			ToolTip.SetTip(OutEstimatedTime,
				"Render speed depends on this machine, the GPU and the footage - it's measured during the first full render and used for the estimate from then on.");
		}

		// A changed setting (frame limit, input file) invalidates whatever was measured from a
		// previous export, so fall back to the plan until the next render actually produces a file.
		OutPlanText.IsVisible = true;
		SetPlannedFramesVisible(true);
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
		SetPlannedFramesVisible(false);
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
		OutMeasuredFileSize.Text = FormatHelper.FormatBytes(new FileInfo(outputPath).Length);
	}

	private static void SetMatchCheck(IconView icon, bool matches)
	{
		icon.Data = matches ? Icons.Check : Icons.Close;
		icon.Foreground = matches ? Palette.Success : Palette.Danger;
		ToolTip.SetTip(StatusRow(icon), matches ? "Matches the source file." : "Differs from the source file.");
	}

	/// <summary>
	///     The value + icon row a status icon sits in - the tooltip goes there so it shows over the whole row,
	///     not just the icon itself (it's only hit-testable where it actually draws).
	/// </summary>
	private static Control StatusRow(IconView icon)
	{
		return icon.Parent as Control ?? icon;
	}

	private void SetPlannedFramesVisible(bool visible)
	{
		OutFrameCountLabel.IsVisible = visible;
		OutFrameCount.IsVisible = visible;
		OutEstimatedTimeLabel.IsVisible = visible;
		OutEstimatedTime.IsVisible = visible;
	}

	/// <summary>
	///     What the render will match of the source, given the current export options (see
	///     FfmpegPipeline.StartRender) - "1:1" only while every option is at its source-matching default,
	///     otherwise it names exactly what deviates, so the card never claims more than is true.
	/// </summary>
	private static string DescribeEncodePlan(OverlaySettings settings, string encoder)
	{
		List<string> deviations = [];
		if (Math.Abs(settings.OutputBitrateMultiplier - 1.0) > 0.001)
			deviations.Add($"bitrate {settings.OutputBitrateMultiplier:0.##}x the source");
		if (encoder == "hevc_nvenc" && settings.NvencPreset != "p7")
			deviations.Add($"faster encoder preset ({settings.NvencPreset.ToUpperInvariant()})");
		if (encoder == "libx265")
			deviations.Add("CPU encoder (x265) instead of the GPU");

		var extras = new List<string>();
		if (settings.PreserveCameraMetadata && (settings.MetadataKeepTelemetry || settings.MetadataKeepDebugTrack || settings.MetadataKeepThumbnails))
			extras.Add(settings.MetadataKeepSerialNumber ? "camera metadata kept, incl. serial number" : "camera metadata kept");
		if (settings.FastStart) extras.Add("fast start");
		var suffix = extras.Count > 0 ? $" ({string.Join(", ", extras)})" : "";

		return deviations.Count == 0
			? $"Encoding matches the source 1:1 - resolution, frame rate, codec/profile/level, bitrate, keyframes and color tags{suffix}."
			: $"Matches the source except: {string.Join(", ", deviations)}{suffix}.";
	}

	private static string FormatFps(double fps)
	{
		return fps.ToString("0.##");
	}
}
