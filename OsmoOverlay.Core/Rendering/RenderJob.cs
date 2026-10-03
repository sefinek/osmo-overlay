using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Ffmpeg;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Preview;
using OsmoOverlay.Core.Reframe;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core;

public sealed record RenderOptions(
	IReadOnlyList<string> InputPaths,
	string OutputPath,
	int? FrameLimit = null,
	string? Encoder = null,
	IReadOnlyList<TelemetryFrame>? TelemetryFrames = null,
	bool Overwrite = true,
	IReadOnlyList<OverlayElement>? Layout = null,
	bool? ShowWatermark = null,
	bool? SmoothGpsMotion = null,
	string? CameraModel = null,
	bool GreenScreen = false,
	// Seconds on the combined timeline of all inputs, rounded to the nearest frame; null = from the
	// start / to the end. CutOuts are removed from inside that range (see RenderPlan.Resolve), and
	// FrameLimit still caps the frame count on top of both.
	double? RangeStartSeconds = null,
	double? RangeEndSeconds = null,
	IReadOnlyList<TimeRange>? CutOuts = null,
	// Where the flat picture of a 360 recording looks - the default (leveled, straight ahead) when null; ignored for a flat video.
	ReframeView? Reframe = null)
{
	public static string DefaultOutputPath(IReadOnlyList<string> inputPaths, string? outputFolder = null)
	{
		string inputPath = inputPaths[0];
		string dir = !string.IsNullOrWhiteSpace(outputFolder) && Directory.Exists(outputFolder) ? outputFolder : Path.GetDirectoryName(inputPath) ?? ".";
		string name = Path.GetFileNameWithoutExtension(inputPath);
		return Path.Combine(dir, $"{name}_overlay.mp4");
	}

	/// <summary>
	///     Derives the green-screen sibling of an already-chosen normal output path (same directory,
	///     "_greenscreen" instead of whatever suffix the normal path used) rather than starting fresh
	///     from the input paths, so it still respects a location the user picked via "..." for the
	///     normal render instead of silently writing somewhere else.
	/// </summary>
	public static string GreenScreenOutputPath(string outputPath)
	{
		string dir = Path.GetDirectoryName(outputPath) ?? ".";
		string name = Path.GetFileNameWithoutExtension(outputPath);
		string ext = Path.GetExtension(outputPath);
		if (name.EndsWith("_overlay", StringComparison.OrdinalIgnoreCase))
			name = name[..^"_overlay".Length];
		return Path.Combine(dir, $"{name}_greenscreen{ext}");
	}
}

public enum RenderPhase
{
	Probing,
	ExtractingTelemetry,
	SelectingEncoder,
	Rendering,
	Done,
	Failed
}

public sealed record RenderStatus(
	RenderPhase Phase,
	string Message,
	int CurrentFrame,
	int TotalFrames,
	TimeSpan Elapsed);

public sealed record RenderResult(bool Success, string? ErrorMessage, TimeSpan Elapsed);

public static class RenderJob
{
	// How many rendered-but-not-yet-written frames the producer may get ahead of ffmpeg by. Each one
	// is a full native-resolution BGRA frame (tens of MB at 4K), so this stays modest - just enough
	// to overlap render and encode, not to buffer a meaningful chunk of the render in memory.
	private const int RenderPrefetchFrames = 3;
	private const int MinFramesForSpeedHistory = 600;

	public static Task<RenderResult> RunAsync(RenderOptions options, IProgress<RenderStatus>? progress,
		CancellationToken ct)
	{
		// Not Task.Run's own token: a cancellation before it starts would throw instead of returning a RenderResult.
		return Task.Run(() => RunAsyncCore(options, progress, ct));
	}

	private static async Task<RenderResult> RunAsyncCore(RenderOptions options, IProgress<RenderStatus>? progress,
		CancellationToken ct)
	{
		var sw = Stopwatch.StartNew();

		void Report(RenderPhase phase, string message, int current = 0, int total = 0)
		{
			progress?.Report(new RenderStatus(phase, message, current, total, sw.Elapsed));
		}

		foreach (string path in options.InputPaths)
		{
			if (!File.Exists(path))
				return new RenderResult(false, string.Format(CoreStrings.Render_FileNotFound, path), sw.Elapsed);
		}

		try
		{
			Report(RenderPhase.Probing, CoreStrings.Render_Probing);
			IReadOnlyList<VideoSegment> segments = VideoSegments.ProbeAll(options.InputPaths);
			if (VideoSegments.FindMismatch(segments) is { } mismatch)
				return new RenderResult(false, mismatch, sw.Elapsed);
			VideoSegment first = segments[0];

			Report(RenderPhase.Probing,
				$"{first.Source.Video.CodecName} {first.Source.Video.Profile}, {first.Source.Video.Width}x{first.Source.Video.Height}, " +
				$"{first.Source.Video.FrameRate} fps, {first.Source.Video.PixFmt}, ~{first.Source.Video.BitRate / 1_000_000} Mbps" +
				(segments.Count > 1 ? " (" + Plural.Format(CoreStrings.Render_Segments, segments.Count) + ")" : ""));

			if (!segments.AllHaveTelemetry())
			{
				return new RenderResult(false, string.Format(CoreStrings.Render_NoTelemetry, first.InputPath), sw.Elapsed);
			}

			if (!options.Overwrite && File.Exists(options.OutputPath))
				return new RenderResult(false, string.Format(CoreStrings.Render_OutputExists, options.OutputPath), sw.Elapsed);

			IReadOnlyList<TelemetryFrame> rawFrames;
			string? cameraModel = options.CameraModel;
			ICameraFormat camera = first.Source.Camera!.Format;
			if (options.TelemetryFrames is { Count: > 0 })
			{
				rawFrames = options.TelemetryFrames;
				Report(RenderPhase.ExtractingTelemetry, string.Format(CoreStrings.Render_UsingExtractedSamples, rawFrames.Count));
			}
			else
			{
				Report(RenderPhase.ExtractingTelemetry, CoreStrings.Render_Extracting);
				TelemetryExtractionResult extraction = TelemetryExtraction.ExtractCombined(segments);
				rawFrames = extraction.Frames;
				cameraModel ??= extraction.CameraModel;
				if (rawFrames.Count == 0)
					return new RenderResult(false, CoreStrings.Render_NoSamples, sw.Elapsed);
				Report(RenderPhase.ExtractingTelemetry, string.Format(CoreStrings.Render_Extracted, rawFrames.Count));
			}

			OverlaySettings settings = OverlaySettingsStore.Load();
			bool smoothGps = options.SmoothGpsMotion ?? settings.SmoothGpsMotion;
			double fps = first.Source.Video.Fps;
			var plan = RenderPlan.Resolve(options.RangeStartSeconds, options.RangeEndSeconds, options.CutOuts, options.FrameLimit,
				fps, segments.TotalFrameCount());
			// The overlay describes the video as rendered: telemetry moved onto the output's own timeline.
			List<DerivedFrame> derived = new OutputTimeline(plan, fps).MapFrames(TelemetryProcessor.Process(rawFrames, camera, smoothGps, settings.SpeedCorrectionPercent));
			double startAltitude = derived[0].Raw.AltitudeMeters;
			double maxSpeedKmh = TelemetryProcessor.Summarize(derived).MaxSpeedKmh;

			VideoInfo output = OutputVideo.For(first.Source.Video, settings);
			if (output != first.Source.Video)
				AppLogger.Info($"Output: {output.CodecName} {output.Width}x{output.Height} {output.PixFmt}, ~{output.BitRate / 1_000_000} Mbps");
			// An encoder picked before Settings changed the output's codec doesn't write it any more.
			string? requested = options.Encoder is { } picked && FfmpegPipeline.Encodes(picked, output) ? picked : null;
			if (requested is null)
				Report(RenderPhase.SelectingEncoder, CoreStrings.Render_CheckingGpuEncoder);
			string encoder = requested ?? FfmpegPipeline.SelectVideoEncoder(output);
			Report(RenderPhase.SelectingEncoder,
				string.Format(FfmpegPipeline.IsGpuEncoder(encoder) ? CoreStrings.Render_EncoderGpu : CoreStrings.Render_EncoderCpu, encoder));

			int totalFrames = (int)plan.TotalFrames;
			Report(RenderPhase.Rendering, plan.IsPartial
				? string.Format(CoreStrings.Render_RenderingPartial, totalFrames, options.OutputPath, DescribePlan(plan, fps))
				: string.Format(CoreStrings.Render_Rendering, totalFrames, options.OutputPath), 0, totalFrames);

			IReadOnlyList<OverlayElement> layout =
				options.Layout ?? LoadActiveLayout();
			// Forces off any widget this file's telemetry can't support (e.g. Map/Compass checked from
			// a previous, GPS-capable file) instead of burning a "--"/0/placeholder into the export -
			// same filter PreviewPlayer applies for the live preview, see OverlayDataRequirements.
			var availability = OverlayAvailability.Of(rawFrames, first.Source.ContainerCreationTimeUtc is not null, camera);
			bool hasGpsFix = availability.GpsFix;
			layout = availability.Apply(layout);
			bool showWatermark = options.ShowWatermark ?? settings.ShowWatermark;
			using var renderer = new OverlayRenderer(output.Width, output.Height,
				startAltitude, layout, derived, maxSpeedKmh, showWatermark, cameraModel,
				first.Source.ContainerCreationTimeUtc, MapSources.From(settings), RouteIntroSettings.ForRecording(settings, hasGpsFix))
			{
				RouteAcrossCuts = settings.RouteAcrossCuts,
				OutputDurationSeconds = plan.TotalFrames / fps
			};

			if (layout.Any(e => e is MapWidgetElement { Visible: true }))
			{
				Report(RenderPhase.Rendering, CoreStrings.Render_FetchingTiles);
				try
				{
					await renderer.PrepareMapAsync(
						(fetched, total) => Report(RenderPhase.Rendering, string.Format(CoreStrings.Render_FetchingTilesProgress, fetched, total), fetched, total),
						ct);
				}
				catch (OperationCanceledException)
				{
					return new RenderResult(false, CoreStrings.Render_Cancelled, sw.Elapsed);
				}
			}

			if (settings.ShowRouteIntro && hasGpsFix)
			{
				Report(RenderPhase.Rendering, CoreStrings.Render_FetchingOverview);
				try
				{
					await renderer.PrepareRouteIntroMapAsync(
						(fetched, total) => Report(RenderPhase.Rendering, string.Format(CoreStrings.Render_FetchingOverviewProgress, fetched, total), fetched,
							total),
						ct);
				}
				catch (OperationCanceledException)
				{
					return new RenderResult(false, CoreStrings.Render_Cancelled, sw.Elapsed);
				}
			}

			// Green screen has no source recording in it to carry camera metadata over from.
			// The camera's data tracks are copied whole (ICameraFormat.CopyMetadata) - on a partial render they'd
			// describe a longer recording than the video they sit next to, so only the non-track parts stay.
			bool keepTracks = !plan.IsPartial;
			if (!keepTracks && (settings.MetadataKeepTelemetry || settings.MetadataKeepDebugTrack) && settings.PreserveCameraMetadata &&
			    !options.GreenScreen)
				Report(RenderPhase.Rendering, CoreStrings.Render_PartialNoTracks);
			var metadataSelection = new CameraMetadataSelection(settings.MetadataKeepTelemetry && keepTracks,
				settings.MetadataKeepDebugTrack && keepTracks, settings.MetadataKeepThumbnails, settings.MetadataKeepSerialNumber);
			bool preserveMetadata = settings.PreserveCameraMetadata && camera.HasMetadataToCopy && metadataSelection.Any && !options.GreenScreen;
			RenderEncodeSettings encode = FfmpegPipeline.EncodeSettingsFrom(settings, preserveMetadata);
			// Only a file this render wrote may be deleted if it fails - never one that was already there and
			// wasn't meant to be overwritten.
			bool outputIsOurs = options.Overwrite || !File.Exists(options.OutputPath);
			bool succeeded = false;
			// A 360 recording's picture is made here (FisheyeProjector, the preview's own) and goes to ffmpeg finished, the
			// overlay drawn on it - ffmpeg only takes the sound from the files.
			Reframer? reframer = options.GreenScreen
				? null
				: Reframer.For(first.Source.Fisheye, rawFrames, camera, options.Reframe ?? new ReframeView());
			using LibavVideoSource? pictures = reframer is null
				? null
				: new LibavVideoSource([.. segments.Select(s => new PlaybackSegment(s.InputPath, s.Source.DurationSeconds))], fps,
					output.Width, output.Height, reframer);
			using Process ffmpeg = FfmpegPipeline.StartRender(segments, options.OutputPath, encoder, output, options.Overwrite, encode,
				plan, options.GreenScreen, pictures is not null);
			Report(RenderPhase.Rendering, ProcessHelper.FormatCommand(ffmpeg.StartInfo.FileName, ffmpeg.StartInfo.ArgumentList));

			// Disposing the Process doesn't terminate it: every path out of the try below must leave ffmpeg dead,
			// or it's orphaned (forever, for a green-screen render with its infinite input).
			try
			{
				Stream stdin = ffmpeg.StandardInput.BaseStream;
				Task<string> stderrTask = ffmpeg.StandardError.ReadToEndAsync(ct);

				// stdin.Write can't be cancelled; killing ffmpeg breaks the pipe and unblocks it as an IOException (caught below).
				using CancellationTokenRegistration killOnCancel = ct.Register(() => KillFfmpegIfRunning(ffmpeg));

				// Drawing (CPU) and feeding ffmpeg (I/O) overlap on two threads. RenderInto() isn't safe to call
				// concurrently - only the producer thread calls it.
				var channel = Channel.CreateBounded<byte[]>(
					new BoundedChannelOptions(RenderPrefetchFrames) { SingleReader = true, SingleWriter = true });

				// Linked so the consumer's finally can always unblock the producer, even when the consumer stopped
				// for a reason other than `ct` (ffmpeg crashing) and nobody drains the channel any more.
				using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
				CancellationToken producerCt = producerCts.Token;

				// Written frames' buffers come back here; the bounded channel caps how many exist.
				var freeBuffers = new ConcurrentQueue<byte[]>();
				int frameBufferSize = renderer.FrameBufferSize();

				var producer = Task.Run(async () =>
				{
					try
					{
						if (pictures is not null)
							await ProduceComposedAsync(pictures, plan, derived, renderer, fps, channel.Writer, producerCt);
						else
							await ProduceOverlayAsync(totalFrames, derived, renderer, fps, freeBuffers, frameBufferSize, channel.Writer, producerCt);

						channel.Writer.TryComplete();
					}
					catch (OperationCanceledException)
					{
						channel.Writer.TryComplete();
					}
					catch (Exception ex)
					{
						channel.Writer.TryComplete(ex);
					}
				}, producerCt);

				int written = 0;
				var rate = new RenderRateEstimator(fps);
				try
				{
					await foreach (byte[] pixels in channel.Reader.ReadAllAsync(ct))
					{
						stdin.Write(pixels, 0, pixels.Length);
						if (pictures is not null) pictures.Recycle(new VideoFrame(pixels, output.Width, output.Height));
						else freeBuffers.Enqueue(pixels);
						written++;
						rate.Add(written);

						if (written % 60 == 0)
						{
							Report(RenderPhase.Rendering, string.Format(CoreStrings.Render_Frame, written, totalFrames) + rate.Describe(written, totalFrames),
								written, totalFrames);
						}
					}

					stdin.Flush();
				}
				catch (OperationCanceledException)
				{
					// The cancelled check below decides whether to report it - the producer may also stop first and end the channel normally.
				}
				catch (IOException)
				{
					// ffmpeg's pipe broke - either it died on its own, or killOnCancel above just killed it
					// because of a cancellation already in flight; the checks below sort out which.
				}
				finally
				{
					producerCts.Cancel();
					stdin.Close();

					try
					{
						await producer;
					}
					catch
					{
						// Any real failure was already observed via the channel (ReadAllAsync rethrows it
						// above) - a second throw here would only be a duplicate.
					}
				}

				bool cancelled = ct.IsCancellationRequested;
				if (cancelled)
				{
					// killOnCancel already killed ffmpeg - closing stdin alone wouldn't stop it: the overlay's eof_action
					// repeats the last frame against the rest of the main input (endless for a green screen). Not ct here,
					// it's cancelled and this wait must finish.
					await ffmpeg.WaitForExitAsync(CancellationToken.None);
					try
					{
						await stderrTask;
					}
					catch (Exception)
					{
						// A killed process's stderr can fault (broken pipe, the token) - it's a cancellation either way.
					}

					return new RenderResult(false, CoreStrings.Render_Cancelled, sw.Elapsed);
				}

				await ffmpeg.WaitForExitAsync(CancellationToken.None);
				string stderr = await stderrTask;

				if (ffmpeg.ExitCode != 0)
				{
					AppLogger.Error(string.Format(CoreStrings.Render_FfmpegFailed, ffmpeg.ExitCode, stderr));
					return new RenderResult(false,
						string.Format(CoreStrings.Render_FfmpegFailedSummary, ffmpeg.ExitCode, FfmpegErrorSummary(stderr)), sw.Elapsed);
				}

				// Short renders are dominated by the route intro and ffmpeg's spin-up, not representative of
				// a full one - only a render long enough to reach steady state updates the estimate.
				if (written >= MinFramesForSpeedHistory && rate.AverageFps(written) is { } averageFps)
				{
					RenderSpeedHistory.Record(RenderSpeedHistory.Key(output.Width, output.Height, fps,
						encoder, encode.NvencPreset), averageFps);
				}

				PostProcess(options.OutputPath, options.InputPaths, camera, preserveMetadata ? metadataSelection : null,
					settings.FastStart && !options.GreenScreen,
					message => Report(RenderPhase.Rendering, message, written, totalFrames));

				succeeded = true;
				return new RenderResult(true, null, sw.Elapsed);
			}
			finally
			{
				KillFfmpegIfRunning(ffmpeg);
				// A cancelled or failed render leaves an MP4 without its moov box - unplayable, and easy to
				// mistake for a finished file next to the source.
				if (!succeeded && outputIsOurs) DeleteIncompleteOutput(ffmpeg, options.OutputPath);
			}
		}
		catch (Exception ex)
		{
			// Single place a render failure is logged/surfaced - callers (GUI, CLI) read it back off
			// RenderResult.ErrorMessage for their own user-facing dialog/console line, but don't log it
			// again themselves, since AppLogger.Error already reaches the file log and (via Notified)
			// the GUI's on-screen panel.
			AppLogger.Error(ex, string.Format(CoreStrings.Render_Failed, string.Join(", ", options.InputPaths), ex.Message));
			return new RenderResult(false, ex.Message, sw.Elapsed);
		}
	}

	/// <summary>The overlay alone on a transparent frame, for ffmpeg to lay over the source's own picture.</summary>
	private static async Task ProduceOverlayAsync(int totalFrames, IReadOnlyList<DerivedFrame> derived, OverlayRenderer renderer, double fps,
		ConcurrentQueue<byte[]> freeBuffers, int frameBufferSize, ChannelWriter<byte[]> writer, CancellationToken ct)
	{
		for (int i = 0; i < totalFrames && !ct.IsCancellationRequested; i++)
		{
			DerivedFrame frame = TelemetryProcessor.FindNearest(derived, i / fps);
			if (!freeBuffers.TryDequeue(out byte[]? pixels)) pixels = new byte[frameBufferSize];
			renderer.RenderInto(frame, pixels);
			await writer.WriteAsync(pixels, ct);
		}
	}

	/// <summary>
	///     A 360 recording's frames for the render: each kept piece decoded on from its first frame, turned into the
	///     flat view and the overlay drawn onto it at the output's time - what the preview shows, at full size.
	/// </summary>
	private static async Task ProduceComposedAsync(LibavVideoSource pictures, RenderPlan plan, IReadOnlyList<DerivedFrame> derived,
		OverlayRenderer renderer, double fps, ChannelWriter<byte[]> writer, CancellationToken ct)
	{
		int outputFrame = 0;
		// The next piece's stream, already running and past its first frames, while an overlapping transition blends them in.
		LibavVideoSource.PlaybackStream? carried = null;
		byte[]? scratch = null;

		VideoFrame Read(LibavVideoSource.PlaybackStream stream)
		{
			return stream.TryReadNextFrame()
			       ?? throw new InvalidOperationException(stream.Error ?? "The recording ended before the render's last frame.");
		}

		for (int p = 0; p < plan.Pieces.Count; p++)
		{
			RenderPiece piece = plan.Pieces[p];
			LibavVideoSource.PlaybackStream stream = carried ?? pictures.OpenPlaybackStream(TimeSpan.FromSeconds(piece.SourceStartFrame / fps), ct);
			carried = null;
			RenderPiece? next = p + 1 < plan.Pieces.Count ? plan.Pieces[p + 1] : null;
			int overlapOut = next?.OverlapIn ?? 0;

			// The first OverlapIn frames were already shown, as the end of the previous piece.
			for (long k = piece.OverlapIn; k < piece.FrameCount - overlapOut && !ct.IsCancellationRequested; k++, outputFrame++)
			{
				VideoFrame frame = Read(stream);
				(double fade, bool white) = CutTransitionFade.At(piece, k, fps);
				CutTransitionFade.Apply(frame.Bgra, fade, white);
				renderer.RenderOnto(TelemetryProcessor.FindNearest(derived, outputFrame / fps), frame.Bgra, frame.Width, frame.Height);
				await writer.WriteAsync(frame.Bgra, ct);
			}

			if (overlapOut == 0 || next is null) continue;

			carried = pictures.OpenPlaybackStream(TimeSpan.FromSeconds(next.SourceStartFrame / fps), ct);
			for (int i = 0; i < overlapOut && !ct.IsCancellationRequested; i++, outputFrame++)
			{
				VideoFrame earlier = Read(stream);
				VideoFrame later = Read(carried);
				scratch ??= new byte[earlier.Bgra.Length];
				CutTransitionBlend.Blend(earlier.Bgra, later.Bgra, scratch, earlier.Width, earlier.Height, next.TransitionIn!.Kind, (double)i / overlapOut);
				pictures.Recycle(later);
				renderer.RenderOnto(TelemetryProcessor.FindNearest(derived, outputFrame / fps), earlier.Bgra, earlier.Width, earlier.Height);
				await writer.WriteAsync(earlier.Bgra, ct);
			}
		}
	}

	/// <summary>
	///     Edits of the finished file ffmpeg can't do itself - the camera's metadata (ICameraFormat.CopyMetadata) and
	///     Mp4FastStart after it. Each step
	///     is best-effort: the video itself is already complete and correct at this point, so a failure is
	///     logged and the render still counts as successful (both steps leave the file intact on failure).
	/// </summary>
	private static void PostProcess(string outputPath, IReadOnlyList<string> inputPaths, ICameraFormat camera, CameraMetadataSelection? metadata,
		bool fastStart, Action<string> report)
	{
		bool preserveMetadata = metadata is not null;
		if (metadata is not null)
		{
			List<string> parts = [];
			if (metadata.Telemetry) parts.Add(metadata.SerialNumber ? CoreStrings.Render_PartTelemetry : CoreStrings.Render_PartTelemetryNoSerial);
			if (metadata.DebugTrack) parts.Add(CoreStrings.Render_PartDebugTrack);
			if (metadata.ThumbnailsAndInfo) parts.Add(CoreStrings.Render_PartThumbnails);
			report(string.Format(CoreStrings.Render_CopyingMetadata, string.Join(", ", parts)));
			try
			{
				camera.CopyMetadata(outputPath, inputPaths, metadata);
			}
			catch (Exception ex)
			{
				AppLogger.Warn(ex, CoreStrings.Render_CopyMetadataFailed);
			}
		}

		// Without preserveMetadata, ffmpeg already wrote the file fast-start itself (-movflags +faststart).
		if (fastStart && preserveMetadata)
		{
			report(CoreStrings.Render_FastStart);
			try
			{
				Mp4FastStart.Apply(outputPath);
			}
			catch (Exception ex)
			{
				AppLogger.Warn(ex, CoreStrings.Render_FastStartFailed);
			}
		}
	}

	/// <summary>
	///     The lines of ffmpeg's output that say what went wrong, for a dialog - the full output (every input described)
	///     goes to the log. An error while running comes after the "Stream mapping" block, among the output's description
	///     (indented) and the progress lines (split by \r); one before it, at the end.
	/// </summary>
	internal static string FfmpegErrorSummary(string stderr, int maxLines = 8)
	{
		string[] lines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
		int mapping = Array.FindLastIndex(lines, l => l.StartsWith("Stream mapping:", StringComparison.Ordinal));
		IEnumerable<string> picked = mapping >= 0 ? lines.Skip(mapping + 1).Where(IsMessage) : lines;
		return string.Join('\n', picked.Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(maxLines));

		static bool IsMessage(string line)
		{
			return !char.IsWhiteSpace(line[0]) &&
			       !line.StartsWith("frame=", StringComparison.Ordinal) &&
			       !line.StartsWith("size=", StringComparison.Ordinal) &&
			       !line.StartsWith("Press [q]", StringComparison.Ordinal) &&
			       !line.StartsWith("Output #", StringComparison.Ordinal);
		}
	}

	/// <summary>"01:00.000 - 01:30.000, 02:10.000 - 05:00.000" - the kept pieces on the recording's timeline.</summary>
	private static string DescribePlan(RenderPlan plan, double fps)
	{
		return string.Join(", ", plan.Pieces.Select(p => $"{TimeText.Format(p.SourceStartFrame / fps)} - {TimeText.Format(p.SourceEndFrame / fps)}"));
	}

	private static void DeleteIncompleteOutput(Process ffmpeg, string outputPath)
	{
		try
		{
			// The killed process may still hold the file for a moment.
			ffmpeg.WaitForExit(5000);
			if (!File.Exists(outputPath)) return;

			File.Delete(outputPath);
			AppLogger.Info($"Removed the incomplete output {outputPath}");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			AppLogger.Warn(ex, string.Format(CoreStrings.Render_RemoveIncompleteFailed, outputPath));
		}
	}

	private static void KillFfmpegIfRunning(Process ffmpeg)
	{
		try
		{
			if (!ffmpeg.HasExited) ffmpeg.Kill(true);
		}
		catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
		{
			// InvalidOperationException: the process exited in the gap between HasExited and Kill.
			// Win32Exception: the OS refused to terminate it (already exiting, access denied, etc.) -
			// this is a best-effort cleanup, not something worth failing the whole render over.
		}
	}

	private static IReadOnlyList<OverlayElement> LoadActiveLayout()
	{
		(List<OverlayPreset> presets, string activeId) = OverlayPresetStore.Load();
		OverlayPreset preset = presets.First(p => p.Id == activeId);
		// Muted (or not soloed) layers stay out of the render as they're out of the preview.
		return OverlayLayers.Drawn(preset.Elements, preset.Layers);
	}
}

/// <summary>
///     Render speed and time-left from a sliding window of recent progress rather than the average since
///     the start - the first seconds (ffmpeg spin-up, encoder lookahead filling, cold caches) run at a
///     very different pace than the rest, and a whole-run average would keep dragging the estimate
///     toward that for the entire render.
/// </summary>
internal sealed class RenderRateEstimator(double sourceFps)
{
	private const double WindowSeconds = 10;
	private const double WarmupSeconds = 3;

	private readonly Queue<(double Seconds, int Frames)> _samples = new();
	private readonly Stopwatch _clock = new();

	public void Add(int framesWritten)
	{
		if (!_clock.IsRunning) _clock.Start();

		double now = _clock.Elapsed.TotalSeconds;
		_samples.Enqueue((now, framesWritten));
		while (_samples.Count > 2 && now - _samples.Peek().Seconds > WindowSeconds)
			_samples.Dequeue();
	}

	/// <summary>Frames per second over the whole render so far (from the first frame written) - what's remembered for the next estimate.</summary>
	public double? AverageFps(int framesWritten)
	{
		double seconds = _clock.Elapsed.TotalSeconds;
		return seconds > 0 ? framesWritten / seconds : null;
	}

	/// <summary>" - 41.3 fps (0.69x realtime) - 00:12:05 left", or " - estimating time left..." during the first few seconds.</summary>
	public string Describe(int framesWritten, int totalFrames)
	{
		if (_clock.Elapsed.TotalSeconds < WarmupSeconds || _samples.Count < 2) return " - " + CoreStrings.Render_Estimating;

		(double Seconds, int Frames) oldest = _samples.Peek();
		double span = _clock.Elapsed.TotalSeconds - oldest.Seconds;
		if (span <= 0) return "";

		double fps = (framesWritten - oldest.Frames) / span;
		if (fps <= 0) return "";

		var left = TimeSpan.FromSeconds(Math.Max(totalFrames - framesWritten, 0) / fps);
		string realtime = sourceFps > 0 ? " " + string.Format(CoreStrings.Render_Realtime, fps / sourceFps) : "";
		return $" - {fps:0.0} fps{realtime} - " + string.Format(CoreStrings.Render_TimeLeft, left.ToString(@"hh\:mm\:ss"));
	}
}
