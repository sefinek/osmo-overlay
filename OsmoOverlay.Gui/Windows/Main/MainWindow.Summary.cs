using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
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
			AppendLog(string.Format(Strings.Summary_LogInput, path));

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
				await ConfirmDialog.ShowAsync(this, Strings.Summary_CantJoinTitle, problem!, kind: DialogKind.Warning);
				return;
			}

			AppendLog(
				$"ffprobe: {summary.Video.CodecName} {summary.Video.Profile}, {summary.Video.Width}x{summary.Video.Height}, " +
				$"{FormatFps(summary.Video.Fps)} fps, {summary.Video.PixFmt}, ~{summary.Video.BitRate / 1_000_000} Mbps");
			AppendLog(
				string.Format(Strings.Summary_LogColor, $"{summary.Video.ColorPrimaries ?? "?"} / {summary.Video.ColorTransfer ?? "?"} / " +
				                                        $"{summary.Video.ColorSpace ?? "?"} ({summary.Video.ColorRange ?? "?"})"));
			if (summary.Fisheye is { } lenses)
			{
				AppendLog(string.Format(Strings.Summary_Log360, $"{lenses.LensSize}x{lenses.LensSize}",
					$"{summary.Video.Width}x{summary.Video.Height}"));
			}

			AppendLog(string.Format(Strings.Summary_LogDuration, TimeSpan.FromSeconds(summary.DurationSeconds).ToString(@"hh\:mm\:ss"),
				FormatHelper.FormatBytes(summary.FileSizeBytes)));
			AppendLog(summary.Audio is { } audio
				? string.Format(Strings.Summary_LogAudio, $"{audio.CodecName}, {audio.SampleRate} Hz, {audio.Channels}ch")
				: Strings.Summary_LogAudioNone);

			AppendLog(string.Format(Strings.Summary_LogCameraModel, summary.CameraModel ?? Strings.Common_Unknown));
			if (summary.Telemetry is not null)
			{
				AppendLog(string.Format(Strings.Summary_LogTelemetryDetected, summary.CameraFormat?.DisplayName ?? Strings.Summary_UnknownCamera,
					summary.TelemetryFrames?.Count ?? 0, summary.DerivedFrames?.Count ?? 0));
			}
			else
			{
				AppendLog(string.Format(Strings.Summary_LogNoTelemetry, string.Join(", ", CameraFormats.All.Select(c => c.DisplayName))),
					LogLevel.Warn);
			}

			if (summary.TelemetryFrames is { Count: > 0 } rawFrames)
			{
				_availability = OverlayAvailability.Of(rawFrames, summary.ContainerRecordingStartUtc is not null, summary.CameraFormat);

				int withGpsSpeed = rawFrames.Count(f => f.GpsSpeedMs is not null);
				AppendLog(withGpsSpeed > 0
					? string.Format(Strings.Summary_LogGpsSpeed, withGpsSpeed, rawFrames.Count, rawFrames.Count - withGpsSpeed)
					: Strings.Summary_LogGpsSpeedNone);

				if (!_availability.GpsFix)
				{
					// No fix anywhere isn't an "anomaly" to flag on the preview timeline - it's just this
					// recording's normal state (e.g. filmed indoors) - see OpenPreviewAsync.
					// Still a real limitation worth flagging in the log (Warn), same amber as the GUI's
					// own "No GPS fix" pill/tooltip elsewhere.
					AppendLog(Strings.Summary_LogNoGps, LogLevel.Warn);
				}
				else
				{
					List<(double Start, double End)> gpsLossRanges = TelemetryProcessor.FindGpsLossRanges(rawFrames);
					if (gpsLossRanges.Count > 0)
					{
						AppendLog(Plural.Format(Strings.Summary_LogGpsLost, gpsLossRanges.Count, gpsLossRanges.Sum(r => r.End - r.Start)),
							LogLevel.Warn);
					}
					else
					{
						AppendLog(Strings.Summary_LogGpsNoLoss);
					}
				}

				if (!_availability.GpsTimestamp)
				{
					AppendLog(_availability.ContainerTime
						? Strings.Summary_LogNoGpsTimeFallback
						: Strings.Summary_LogNoGpsTime, LogLevel.Warn);
				}

				if (summary.CameraFormat is { } camera)
				{
					AppendLog(camera.DescribeTelemetry(rawFrames));
					if (!_availability.CameraAxes)
					{
						AppendLog(Strings.Summary_LogNoCameraAxes, LogLevel.Warn);
					}
				}
			}

			if (summary.Telemetry is { } tele)
			{
				AppendLog(string.Format(Strings.Summary_LogTelemetrySummary, tele.TotalDistanceMeters / 1000.0, tele.MaxSpeedKmh,
					tele.MinAltitudeMeters, tele.MaxAltitudeMeters, tele.MaxGForce));
				AppendLog(tele.RecordedAtUtc is { } recordedUtc
					? string.Format(Strings.Summary_LogRecordedAt, recordedUtc.ToLocalFromUtc().ToString("yyyy-MM-dd HH:mm:ss"))
					: Strings.Summary_LogRecordedAtUnknown);
			}

			AppendLog(Strings.Summary_LogCheckingNvenc);
			string encoder = await Task.Run(() => FfmpegPipeline.SelectVideoEncoder(summary.Video));
			AppendLog(string.Format(FfmpegPipeline.IsGpuEncoder(encoder) ? Strings.Summary_LogEncoderGpu : Strings.Summary_LogEncoderCpu, encoder));

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
			AppendLog(string.Format(Strings.Summary_LogReadFailed, ex.Message), LogLevel.Error);
			SetPhase(UiPhase.Idle);
			ActionButton.IsEnabled = true;
		}
		finally
		{
			TaskbarProgress.SetState(this, TaskbarProgress.State.NoProgress);
		}
	}

	/// <summary>
	///     Once per set of files (not again when the same recording is read again): the camera's SupportNotice, or else what the
	///     recording lacks - no telemetry at all, or no GPS fix - since the log alone is easy to miss. A camera with a
	///     SupportNotice (Insta360: no GPS read yet) says the second already.
	/// </summary>
	private async Task ShowSupportNoticeAsync(FileSummary summary, List<string> inputPaths)
	{
		string key = string.Join('|', inputPaths);
		if (key == _supportNoticeShownFor) return;
		_supportNoticeShownFor = key;

		if (summary.CameraFormat?.SupportNotice(summary.CameraModel) is { } notice)
		{
			AppendLog(notice, LogLevel.Warn);
			await ConfirmDialog.ShowAsync(this, string.Format(Strings.Summary_LimitedSupportTitle, summary.CameraFormat.DisplayName), notice,
				kind: DialogKind.Warning);
		}
		else if (!summary.HasTelemetry) await ShowNoTelemetryNoticeAsync();
		else if (!_availability.GpsFix) await ShowNoGpsNoticeAsync();
	}

	internal Task ShowNoTelemetryNoticeAsync()
	{
		return ConfirmDialog.ShowAsync(this, Strings.Summary_NoTelemetryTitle,
			string.Format(Strings.Summary_NoTelemetryMessage, string.Join(", ", CameraFormats.All.Select(c => c.DisplayName))),
			kind: DialogKind.Warning);
	}

	internal Task ShowNoGpsNoticeAsync()
	{
		return ConfirmDialog.ShowAsync(this, Strings.Summary_NoGpsTitle, Strings.Summary_NoGpsMessage, kind: DialogKind.Warning,
			art: DialogArt.NoGps);
	}

	private void PopulateInputInfo(FileSummary summary)
	{
		InfoCamera.Text = summary.CameraModel ?? Strings.Common_UnknownCapital;

		var recommended = RecommendedSettings.ForCameraModel(summary.CameraModel);

		bool? Check(Func<RecommendedSettings, bool> predicate)
		{
			return recommended is { } r ? predicate(r) : null;
		}

		InfoResolution.Text = $"{summary.Video.Width}x{summary.Video.Height}";
		SetCheck(InfoResolutionCheck, Check(r => summary.Video.Width >= r.Width && summary.Video.Height >= r.Height),
			recommended is { } r1 ? string.Format(Strings.Summary_OrHigher, $"{r1.Width}x{r1.Height}") : null);

		InfoFrameRate.Text = $"{FormatFps(summary.Video.Fps)} fps";
		SetCheck(InfoFrameRateCheck, Check(r => Math.Abs(summary.Video.Fps - r.Fps) < 0.5),
			recommended is { } r2 ? $"~{r2.Fps:0.##} fps" : null);

		InfoCodec.Text = string.IsNullOrEmpty(summary.Video.Profile)
			? summary.Video.CodecName
			: $"{summary.Video.CodecName} ({summary.Video.Profile})";
		ToolTip.SetTip(InfoCodec, string.Format(Strings.Summary_PixelFormatTip, summary.Video.PixFmt));
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
			Strings.Summary_RecommendedColor);

		InfoBitrate.Text = $"{summary.Video.BitRate / 1_000_000.0:0.#} Mbps";
		SetCheck(InfoBitrateCheck, Check(r => summary.Video.BitRate >= r.MinVideoBitrate),
			recommended is { } r3 ? string.Format(Strings.Summary_AtLeast, $"{r3.MinVideoBitrate / 1_000_000.0:0.#} Mbps") : null);

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
				recommended is { } r4 ? string.Format(Strings.Summary_AtLeast, $"{r4.MinAudioBitrate / 1000.0:0} kbps") : null);
		}

		// "Detected" alone would read as "full telemetry" even for a recording that never had a GPS
		// fix (accelerometer/camera-settings data only) - a
		// distinct amber "No GPS fix" state instead of lumping it in with the green case, so it isn't
		// mistaken for a recording with real position data.
		bool hasGpsFix = summary.TelemetryFrames is { Count: > 0 } telemetryFrames && TelemetryProcessor.HasAnyGpsFix(telemetryFrames);
		IBrush telemetryBrush = !summary.HasTelemetry ? Palette.Danger : hasGpsFix ? Palette.Success : Palette.Warning;
		InfoTelemetry.Text = !summary.HasTelemetry ? Strings.Summary_TelemetryNotFound
			: hasGpsFix ? Strings.Summary_TelemetryDetected
			: Strings.Summary_TelemetryNoGpsFix;
		InfoTelemetry.Foreground = telemetryBrush;
		TelemetryPill.Background = Palette.Tint(telemetryBrush, 0.24);
		ToolTip.SetTip(TelemetryPill, summary.HasTelemetry && !hasGpsFix
			? Strings.Summary_TelemetryNoGpsFixTip
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
			true => string.Format(Strings.Summary_RecommendedOk, recommendedDescription),
			false => string.Format(Strings.Summary_RecommendedNot, recommendedDescription),
			null => null
		});
	}

	private void PopulateTelemetryInfo(FileSummary summary)
	{
		if (summary.Telemetry is not { } t) return;

		TeleSamples.Text = $"{t.SampleCount}";
		TeleDuration.Text = TimeSpan.FromSeconds(t.DurationSeconds).ToString(@"hh\:mm\:ss");
		TeleMaxG.Text = $"{t.MaxGForce:0.00} G";
		TeleRecordedAt.Text = t.RecordedAtUtc is { } utc ? utc.ToLocalFromUtc().ToString("yyyy-MM-dd HH:mm:ss") : Strings.Common_UnknownCapital;

		// Without a GPS fix there's nothing to measure: a dash, not a "0.00 km" that reads like a recording that never moved.
		bool hasGps = _availability.GpsFix;
		TeleDistance.Text = hasGps ? $"{t.TotalDistanceMeters / 1000.0:0.00} km" : "-";
		TeleAltitude.Text = hasGps ? $"{t.MinAltitudeMeters:0} - {t.MaxAltitudeMeters:0} m" : "-";
		string? noGpsFixTip = hasGps ? null : Strings.Summary_NoGpsFixTip;
		ToolTip.SetTip(TeleDistance, noGpsFixTip);
		ToolTip.SetTip(TeleAltitude, noGpsFixTip);
		ShowMaxSpeedInfo();
	}

	/// <summary>
	///     The top speed as the overlay shows it - processed like the preview, with the speed correction - not the cached
	///     summary's measured one, so this card and the speed widgets agree. Again after Settings changes either.
	/// </summary>
	private void ShowMaxSpeedInfo()
	{
		if (!_availability.GpsFix || RecordingSpeeds().PeakKmh is not { } measured)
		{
			TeleMaxSpeed.Text = "-";
			ToolTip.SetTip(TeleMaxSpeed, _summary?.Telemetry is null ? null : Strings.Summary_NoGpsFixTip);
			return;
		}

		double percent = OverlaySettingsStore.Load().SpeedCorrectionPercent;
		TeleMaxSpeed.Text = $"{SpeedCalibration.Corrected(measured, percent):0.#} km/h";
		ToolTip.SetTip(TeleMaxSpeed, percent > 0
			? string.Format(Strings.Summary_MaxSpeedCorrectedTip, measured, percent)
			: Strings.Summary_MaxSpeedTip);
	}

	private void LogCacheEvent(FileSummaryCacheEvent e)
	{
		switch (e.Kind)
		{
			case FileSummaryCacheEventKind.Hit:
				AppendLog(string.Format(Strings.Summary_LogCacheHit, e.CurrentFormatVersion));
				return;
			case FileSummaryCacheEventKind.Stale:
				AppendLog(string.Format(Strings.Summary_LogCacheStale, e.PreviousFormatVersion, e.CurrentFormatVersion,
					FormatHelper.FormatBytes(e.PreviousSizeBytes)), LogLevel.Warn);
				break;
			case FileSummaryCacheEventKind.Miss:
				AppendLog(Strings.Summary_LogCacheMiss);
				break;
			case FileSummaryCacheEventKind.Saved:
				AppendLog(string.Format(Strings.Summary_LogCacheSaved, e.CurrentFormatVersion));
				return;
			case FileSummaryCacheEventKind.SaveFailed:
				AppendLog(Strings.Summary_LogCacheSaveFailed, LogLevel.Warn);
				return;
		}

		AppendLog(Strings.Summary_LogProbing);
		AppendLog(Strings.Summary_LogExtracting);
	}

	private void PopulateOutputInfo(FileSummary summary, string encoder)
	{
		double fps = summary.Video.Fps;
		long totalFrames = PlannedFrameCount();

		OutEncoder.Text = encoder + (FfmpegPipeline.IsGpuEncoder(encoder) ? " (GPU)" : " (CPU)");
		// Same condition FfmpegPipeline uses: pieces joined by the concat filter can't have their audio stream-copied.
		OutAudio.Text = summary.Audio is null ? Strings.Main_None
			: _outputTimeline?.Plan.Pieces.Count > 1 ? Strings.Summary_AudioReencoded
			: Strings.Summary_AudioCopied;
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
				string.Format(Strings.Summary_EstimateTip, encoder, renderFps, renderFps / fps));
		}
		else
		{
			OutEstimatedTime.Text = Strings.Summary_EstimateUnknown;
			ToolTip.SetTip(OutEstimatedTime, Strings.Summary_EstimateUnknownTip);
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
			AppendLog(string.Format(Strings.Summary_LogVerifyFailed, ex.Message));
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
		ToolTip.SetTip(StatusRow(icon), matches ? Strings.Summary_MatchesSource : Strings.Summary_DiffersFromSource);
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
			deviations.Add(string.Format(Strings.Summary_PlanBitrate, settings.OutputBitrateMultiplier));
		if (FfmpegPipeline.IsGpuEncoder(encoder) && settings.NvencPreset != "p7")
			deviations.Add(string.Format(Strings.Summary_PlanFasterPreset, settings.NvencPreset.ToUpperInvariant()));
		if (!FfmpegPipeline.IsGpuEncoder(encoder))
			deviations.Add(string.Format(Strings.Summary_PlanCpuEncoder, encoder));

		var extras = new List<string>();
		if (settings.PreserveCameraMetadata && (settings.MetadataKeepTelemetry || settings.MetadataKeepDebugTrack || settings.MetadataKeepThumbnails))
			extras.Add(settings.MetadataKeepSerialNumber ? Strings.Summary_PlanMetadataKeptSerial : Strings.Summary_PlanMetadataKept);
		if (settings.FastStart) extras.Add(Strings.Summary_PlanFastStart);
		string suffix = extras.Count > 0 ? $" ({string.Join(", ", extras)})" : "";

		return deviations.Count == 0
			? string.Format(Strings.Summary_PlanMatches, suffix)
			: string.Format(Strings.Summary_PlanMatchesExcept, string.Join(", ", deviations), suffix);
	}

	private static string FormatFps(double fps)
	{
		return fps.ToString("0.##");
	}
}
