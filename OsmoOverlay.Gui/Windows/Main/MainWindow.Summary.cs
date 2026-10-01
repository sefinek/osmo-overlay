using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;
using OsmoOverlay.Gui.Native;

namespace OsmoOverlay.Gui;

/// <summary>"Get Summary" (probing/telemetry extraction) and populating the Input/Telemetry/Output info cards from the resulting FileSummary.</summary>
public partial class MainWindow
{
	private async Task RunGetSummaryAsync()
	{
		if (_inputPaths.Count == 0 || _inputPaths.Any(p => !File.Exists(p)))
			return;

		List<string> inputPaths = [.. _inputPaths];

		ClosePreview();
		ResetReframe();
		SetPhase(UiPhase.LoadingSummary);
		_availability = new OverlayAvailability(false, false, false, false, false);
		// Cuts, thumbnails and the waveform belong to the recording they were made for.
		ClearCuts();
		ClearMoments();
		ReleaseTimelineTracks();
		ClearLogPanels();
		// Indeterminate rather than a percentage - probing/extraction/preview-open (which may itself
		// fetch map tiles) has no single reliable "done fraction" to report, unlike the render below.
		TaskbarProgress.SetState(this, TaskbarProgress.State.Indeterminate);
		foreach (string path in inputPaths)
			AppendLog($"Input: {path}");

		// Cache events arrive on the worker thread, mid-Read - posted through the same UI context the
		// await below resumes on, so they land in the log in order and before the results.
		SynchronizationContext? ui = SynchronizationContext.Current;

		try
		{
			(FileSummary? read, string? problem) = await Task.Run(() => FileSummaryReader.Read(inputPaths,
				cacheEvent => ui?.Post(_ => LogCacheEvent(cacheEvent), null)));
			if (read is not { } summary)
			{
				AppendLog(problem!, LogLevel.Error);
				SetPhase(UiPhase.Idle);
				ActionButton.IsEnabled = true;
				await ConfirmDialog.ShowAsync(this, "These files can't be joined", problem!, kind: DialogKind.Warning);
				return;
			}

			AppendLog(
				$"ffprobe: {summary.Video.CodecName} {summary.Video.Profile}, {summary.Video.Width}x{summary.Video.Height}, " +
				$"{FormatFps(summary.Video.Fps)} fps, {summary.Video.PixFmt}, ~{summary.Video.BitRate / 1_000_000} Mbps");
			AppendLog(
				$"Color: {summary.Video.ColorPrimaries ?? "?"} / {summary.Video.ColorTransfer ?? "?"} / " +
				$"{summary.Video.ColorSpace ?? "?"} ({summary.Video.ColorRange ?? "?"})");
			if (summary.Fisheye is { } lenses)
			{
				AppendLog($"360 recording: two {lenses.LensSize}x{lenses.LensSize} fisheye lenses, framed into a flat " +
				          $"{summary.Video.Width}x{summary.Video.Height} picture - pick the view with the globe above the preview");
			}

			AppendLog($"Duration: {TimeSpan.FromSeconds(summary.DurationSeconds):hh\\:mm\\:ss}, size: {FormatHelper.FormatBytes(summary.FileSizeBytes)}");
			AppendLog(summary.Audio is { } audio
				? $"Audio: {audio.CodecName}, {audio.SampleRate} Hz, {audio.Channels}ch"
				: "Audio: none");

			AppendLog($"Camera model: {summary.CameraModel ?? "unknown"}");
			if (summary.Telemetry is not null)
			{
				AppendLog($"Telemetry detected ({summary.CameraFormat?.DisplayName ?? "unknown camera"}) - " +
				          $"{summary.TelemetryFrames?.Count ?? 0} raw samples, {summary.DerivedFrames?.Count ?? 0} derived frames");
			}
			else
			{
				AppendLog("No telemetry found - this file can be played but not rendered. Add the camera's original recording " +
				          $"({string.Join(", ", CameraFormats.All.Select(c => c.DisplayName))}), not a file exported from another app", LogLevel.Warn);
			}

			if (summary.TelemetryFrames is { Count: > 0 } rawFrames)
			{
				_availability = OverlayAvailability.Of(rawFrames, summary.ContainerRecordingStartUtc is not null, summary.CameraFormat);

				int withGpsSpeed = rawFrames.Count(f => f.GpsSpeedMs is not null);
				AppendLog(withGpsSpeed > 0
					? $"GPS-measured speed: {withGpsSpeed}/{rawFrames.Count} frames (the GPS receiver's own velocity); " +
					  $"{rawFrames.Count - withGpsSpeed} fall back to derived speed"
					: "GPS-measured speed: not available for this file - using derived speed for all frames");

				if (!_availability.GpsFix)
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
					{
						AppendLog($"GPS signal lost: {gpsLossRanges.Count} range(s), {gpsLossRanges.Sum(r => r.End - r.Start):0.0}s total " +
						          "(shown as amber marks under the preview timeline)", LogLevel.Warn);
					}
					else
					{
						AppendLog("GPS signal: no loss detected");
					}
				}

				if (!_availability.GpsTimestamp)
				{
					AppendLog(_availability.ContainerTime
						? "GPS timestamp: not present in this recording - Date & time / UTC time fall back to the " +
						  "file's own recording-start time instead (approximate, not GPS-synced; flagged with a warning icon in the widget list)"
						: "GPS timestamp: not present in this recording, and no usable recording-start time either - " +
						  "Date & time / UTC time are unavailable and greyed out", LogLevel.Warn);
				}

				if (summary.CameraFormat is { } camera)
				{
					AppendLog(camera.DescribeTelemetry(rawFrames));
					if (!_availability.CameraAxes)
					{
						AppendLog("Roll, pitch and G-meter widgets aren't available for this camera - its accelerometer's axes aren't " +
						          "known against the picture", LogLevel.Warn);
					}
				}
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
			string encoder = await Task.Run(() => FfmpegPipeline.SelectVideoEncoder(summary.Video));
			AppendLog($"Using encoder: {encoder}" + (FfmpegPipeline.IsGpuEncoder(encoder) ? " (GPU)" : " (NVENC unavailable - CPU)"));

			_summary = summary;
			_detectedEncoder = encoder;

			PopulateInputInfo(summary);
			PopulateTelemetryInfo(summary);
			PopulateOutputInfo(summary, encoder);

			SetPhase(UiPhase.SummaryReady);
			ActionButton.IsEnabled = summary.HasTelemetry;
			GreenScreenButton.IsEnabled = summary.HasTelemetry;

			await OpenPreviewAsync(summary);
			ResetHistory();

			await ShowSupportNoticeAsync(summary, inputPaths);
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

	/// <summary>The camera's SupportNotice, once per set of files - not again when the same recording is read again.</summary>
	private async Task ShowSupportNoticeAsync(FileSummary summary, List<string> inputPaths)
	{
		if (summary.CameraFormat?.SupportNotice(summary.CameraModel) is not { } notice) return;

		string key = string.Join('|', inputPaths);
		if (key == _supportNoticeShownFor) return;
		_supportNoticeShownFor = key;

		AppendLog(notice, LogLevel.Warn);
		await ConfirmDialog.ShowAsync(this, $"Limited {summary.CameraFormat.DisplayName} support", notice, kind: DialogKind.Warning);
	}

	private void PopulateInputInfo(FileSummary summary)
	{
		InfoCamera.Text = summary.CameraModel ?? "Unknown";

		var recommended = RecommendedSettings.ForCameraModel(summary.CameraModel);

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
		ToolTip.SetTip(InfoCodec, $"Pixel format: {summary.Video.PixFmt}");
		SetCheck(InfoCodecCheck,
			Check(_ => summary.Video.CodecName.Equals("hevc", StringComparison.OrdinalIgnoreCase) &&
			           summary.Video.PixFmt.Contains("10le", StringComparison.OrdinalIgnoreCase)),
			"HEVC (H.265), 10-bit");

		string primaries = summary.Video.ColorPrimaries ?? "?";
		string transfer = summary.Video.ColorTransfer ?? "?";
		string colorSpace = summary.Video.ColorSpace ?? "?";
		string range = summary.Video.ColorRange ?? "?";
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
		// fix (accelerometer/camera-settings data only) - a
		// distinct amber "No GPS fix" state instead of lumping it in with the green case, so it isn't
		// mistaken for a recording with real position data.
		bool hasGpsFix = summary.TelemetryFrames is { Count: > 0 } telemetryFrames && TelemetryProcessor.HasAnyGpsFix(telemetryFrames);
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
		TeleMaxG.Text = $"{t.MaxGForce:0.00} G";
		TeleRecordedAt.Text = t.RecordedAtUtc is { } utc ? utc.ToLocalFromUtc().ToString("yyyy-MM-dd HH:mm:ss") : "Unknown";

		// Without a GPS fix there's nothing to measure: a dash, not a "0.00 km" that reads like a recording that never moved.
		bool hasGps = _availability.GpsFix;
		TeleDistance.Text = hasGps ? $"{t.TotalDistanceMeters / 1000.0:0.00} km" : "-";
		TeleMaxSpeed.Text = hasGps ? $"{t.MaxSpeedKmh:0.#} km/h" : "-";
		TeleAltitude.Text = hasGps ? $"{t.MinAltitudeMeters:0} - {t.MaxAltitudeMeters:0} m" : "-";
		string? noGpsFixTip = hasGps ? null : "No GPS fix in this recording.";
		ToolTip.SetTip(TeleDistance, noGpsFixTip);
		ToolTip.SetTip(TeleMaxSpeed, noGpsFixTip);
		ToolTip.SetTip(TeleAltitude, noGpsFixTip);
	}

	private void LogCacheEvent(FileSummaryCacheEvent e)
	{
		switch (e.Kind)
		{
			case FileSummaryCacheEventKind.Hit:
				AppendLog($"Cache hit (format v{e.CurrentFormatVersion}): using the cached analysis, file unchanged since last run");
				return;
			case FileSummaryCacheEventKind.Stale:
				AppendLog($"Cache outdated: found format v{e.PreviousFormatVersion}, current is v{e.CurrentFormatVersion} " +
				          $"(telemetry logic changed since). Old cache entry ({FormatHelper.FormatBytes(e.PreviousSizeBytes)}) deleted - generating a new one...", LogLevel.Warn);
				break;
			case FileSummaryCacheEventKind.Miss:
				AppendLog("No cache for this file yet (or the file changed since) - analyzing...");
				break;
			case FileSummaryCacheEventKind.Saved:
				AppendLog($"New cache saved (format v{e.CurrentFormatVersion})");
				return;
			case FileSummaryCacheEventKind.SaveFailed:
				AppendLog("Could not save the cache - see the log; the next run will analyze this file again", LogLevel.Warn);
				return;
		}

		AppendLog("Probing source file(s) (ffprobe)...");
		AppendLog("Extracting telemetry...");
	}

	private void PopulateOutputInfo(FileSummary summary, string encoder)
	{
		double fps = summary.Video.Fps;
		long totalFrames = PlannedFrameCount();

		OutEncoder.Text = encoder + (FfmpegPipeline.IsGpuEncoder(encoder) ? " (GPU)" : " (CPU)");
		// Same condition FfmpegPipeline uses: pieces joined by the concat filter can't have their audio stream-copied.
		OutAudio.Text = summary.Audio is null ? "None"
			: _outputTimeline?.Plan.Pieces.Count > 1 ? "AAC at the source bitrate (re-encoded to join the cuts)"
			: "Copied unchanged";
		OutFrameCount.Text = totalFrames.ToString("N0", CultureInfo.CurrentCulture) + (HasCuts ? $" ({DescribeCuts()})" : "");

		// Speed measured by the last full render of this same shape (RenderSpeedHistory) - there's no
		// meaningful way to predict it before the first one, so say so instead of guessing.
		OverlaySettings settings = OverlaySettingsStore.Load();
		OutPlanText.Text = DescribeEncodePlan(settings, encoder);
		string key = RenderSpeedHistory.Key(summary.Video.Width, summary.Video.Height, fps, encoder, settings.NvencPreset);
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

		// A changed setting (cuts, input files) invalidates whatever was measured from a
		// previous export, so fall back to the plan until the next render actually produces a file.
		OutPlanText.IsVisible = true;
		SetPlannedFramesVisible(true);
		OutMeasuredPanel.IsVisible = false;
	}

	/// <summary>
	///     Replaces the pre-render plan with what ffprobe actually measured from the exported file, so
	///     "matches the source" is a verified fact rather than an assumption baked into the UI text.
	/// </summary>
	private async Task PopulateMeasuredOutputInfoAsync(string outputPath, FileSummary inputSummary)
	{
		SourceInfo output;
		try
		{
			output = await Task.Run(() => SourceProbe.Probe(outputPath));
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

		string primaries = output.Video.ColorPrimaries ?? "?";
		string transfer = output.Video.ColorTransfer ?? "?";
		string colorSpace = output.Video.ColorSpace ?? "?";
		string range = output.Video.ColorRange ?? "?";
		OutColor.Text = primaries == transfer && transfer == colorSpace
			? $"{primaries} ({range})"
			: $"{primaries} / {transfer} / {colorSpace} ({range})";
		SetMatchCheck(OutColorCheck,
			primaries == (inputSummary.Video.ColorPrimaries ?? "?") &&
			transfer == (inputSummary.Video.ColorTransfer ?? "?") &&
			colorSpace == (inputSummary.Video.ColorSpace ?? "?") &&
			range == (inputSummary.Video.ColorRange ?? "?"));

		OutBitrate.Text = $"{output.Video.BitRate / 1_000_000.0:0.#} Mbps";
		// The encoder's rate lands near the source's, not exactly on it - "matches" means close.
		double bitrateRatio = (double)output.Video.BitRate / inputSummary.Video.BitRate;
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
		if (FfmpegPipeline.IsGpuEncoder(encoder) && settings.NvencPreset != "p7")
			deviations.Add($"faster encoder preset ({settings.NvencPreset.ToUpperInvariant()})");
		if (!FfmpegPipeline.IsGpuEncoder(encoder))
			deviations.Add($"CPU encoder ({encoder}) instead of the GPU");

		var extras = new List<string>();
		if (settings.PreserveCameraMetadata && (settings.MetadataKeepTelemetry || settings.MetadataKeepDebugTrack || settings.MetadataKeepThumbnails))
			extras.Add(settings.MetadataKeepSerialNumber ? "camera metadata kept, incl. serial number" : "camera metadata kept");
		if (settings.FastStart) extras.Add("fast start");
		string suffix = extras.Count > 0 ? $" ({string.Join(", ", extras)})" : "";

		return deviations.Count == 0
			? $"Encoding matches the source 1:1 - resolution, frame rate, codec/profile/level, bitrate, keyframes and color tags{suffix}."
			: $"Matches the source except: {string.Join(", ", deviations)}{suffix}.";
	}

	private static string FormatFps(double fps)
	{
		return fps.ToString("0.##");
	}
}
