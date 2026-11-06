using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Telemavix.Core.Ffmpeg;
using Telemavix.Core.Localization;
using Telemavix.Core.Logging;
using Telemavix.Core.Mapping;
using Telemavix.Core.Overlay;
using Telemavix.Core.Telemetry;
using Telemavix.Core.Telemetry.External;

namespace Telemavix.Core;

/// <param name="Format">The picture's size and rate: no file says them (TrackOnlyFormat).</param>
/// <param name="Playback">The track already processed for the preview; used only when it was made with the same GPS smoothing and speed correction as the render's.</param>
/// <param name="Music">Clips on the output's timeline (MusicTrack); the only sound there is.</param>
public sealed record TrackRenderOptions(ExternalTrack Track, TrackOnlyFormat Format, string OutputPath, string? Encoder = null, bool Overwrite = true,
	IReadOnlyList<OverlayElement>? Layout = null, IReadOnlyList<MusicClip>? Music = null, OverlaySettings? Settings = null,
	IReadOnlyList<TimeRange>? CutOuts = null, ExternalTrackPlayback? Playback = null);

/// <summary>
///     A GPS track rendered with no recording (TrackOnlyRecording): the picture the full telemetry preview shows (TrackBackdrop,
///     or black without a map) with the overlay drawn on it here, ffmpeg only encodes. No cuts, no camera metadata.
/// </summary>
public static class TrackRenderJob
{
	private const int PrefetchFrames = 3;
	// A track's picture is a map with a line on it, nothing like footage: well under a camera's rate for the same size.
	private const double BitsPerPixel = 0.1;

	public static Task<RenderResult> RunAsync(TrackRenderOptions options, IProgress<RenderStatus>? progress, CancellationToken ct)
	{
		return Task.Run(() => RunCoreAsync(options, progress, ct));
	}

	private static async Task<RenderResult> RunCoreAsync(TrackRenderOptions options, IProgress<RenderStatus>? progress, CancellationToken ct)
	{
		var sw = Stopwatch.StartNew();

		void Report(RenderPhase phase, string message, int current = 0, int total = 0, string? replaceKey = null)
		{
			progress?.Report(new RenderStatus(phase, message, current, total, sw.Elapsed, replaceKey));
		}

		try
		{
			if (!options.Overwrite && File.Exists(options.OutputPath))
				return new RenderResult(false, string.Format(CoreStrings.Render_OutputExists, options.OutputPath), sw.Elapsed);

			OverlaySettings settings = options.Settings ?? OverlaySettingsStore.Load();
			(FileSummary summary, ExternalTrackPlayback playback) = options.Playback is { IsTrackOnly: true } ready && ready.SmoothGpsMotion == settings.SmoothGpsMotion &&
			                                                          ready.SpeedCorrectionPercent == settings.SpeedCorrectionPercent
				? TrackOnlyRecording.Create(ready, options.Format)
				: TrackOnlyRecording.Create(options.Track, settings, options.Format);
			IReadOnlyList<DerivedFrame> derived = playback.DerivedFrames;
			double fps = options.Format.Fps;
			var plan = RenderPlan.Resolve(null, null, options.CutOuts, null, fps, summary.TotalFrameCount);
			var outputTimeline = new OutputTimeline(plan, fps);
			IReadOnlyList<DerivedFrame> shown = outputTimeline.MapFrames(derived);
			int totalFrames = (int)plan.TotalFrames;
			int width = options.Format.Width, height = options.Format.Height;

			VideoInfo output = summary.Video with
			{
				CodecName = settings.OutputCodec == "hevc" ? "hevc" : "h264",
				BitRate = (long)(width * (double)height * fps * BitsPerPixel * settings.OutputBitrateMultiplier)
			};
			string encoder = options.Encoder is { } picked && FfmpegPipeline.Encodes(picked, output) ? picked : FfmpegPipeline.SelectVideoEncoder(output);
			Report(RenderPhase.SelectingEncoder, string.Format(FfmpegPipeline.IsGpuEncoder(encoder) ? CoreStrings.Render_EncoderGpu : CoreStrings.Render_EncoderCpu,
				encoder, FfmpegPipeline.EncoderCard(encoder) ?? "GPU"));

			IReadOnlyList<OverlayElement> layout = options.Layout ?? ActiveLayout();
			layout = OverlayAvailability.Of(playback.RawFrames, true, null).Apply(layout);

			Report(RenderPhase.Rendering, CoreStrings.Render_FetchingTiles);
			await MapKeyCheck.EnsureAsync(settings.MapApiKeys, ct);
			using TrackBackdrop? backdrop = await TrackBackdrop.BuildAsync(derived, settings, options.Format, ct);

			using var renderer = new OverlayRenderer(width, height, shown[0].Raw.AltitudeMeters, layout, shown, TelemetryProcessor.Summarize(shown).MaxSpeedKmh,
				settings.ShowWatermark, options.Track.DeviceName, summary.ContainerRecordingStartUtc, MapSources.From(settings), RouteIntroSettings.Disabled)
			{
				OutputDurationSeconds = totalFrames / fps,
				CutCaptions = outputTimeline.Captions,
				DrawShadows = !settings.DisableShadows,
				Backdrop = backdrop?.Map
			};
			if (layout.Any(e => e is MapWidgetElement { Visible: true }))
				await renderer.PrepareMapAsync((fetched, total) => Report(RenderPhase.Rendering, string.Format(CoreStrings.Render_FetchingTilesProgress, fetched, total), fetched, total, RenderProgressKeys.Tiles), ct);

			Report(RenderPhase.Rendering, string.Format(CoreStrings.Render_Rendering, totalFrames, options.OutputPath), 0, totalFrames);
			IReadOnlyList<MusicClip>? music = options.Music is { Count: > 0 }
				? MusicTrack.WithCrossfades(MusicTrack.Normalize(options.Music, totalFrames / fps))
				: null;
			RenderEncodeSettings encode = FfmpegPipeline.EncodeSettingsFrom(settings, false);
			bool outputIsOurs = options.Overwrite || !File.Exists(options.OutputPath);
			bool succeeded = false;
			using Process ffmpeg = FfmpegPipeline.StartTrackRender(options.OutputPath, encoder, output, options.Overwrite, encode, options.Track.StartUtc, music, totalFrames / fps);
			Report(RenderPhase.Rendering, ProcessHelper.FormatCommand(ffmpeg.StartInfo.FileName, ffmpeg.StartInfo.ArgumentList));

			try
			{
				var rate = new RenderRateEstimator(options.Format.Fps);
				var freeBuffers = new ConcurrentQueue<byte[]>();
				int bufferSize = renderer.FrameBufferSize();

				FrameFeedResult feed = await FfmpegFrameFeed.RunAsync(ffmpeg, PrefetchFrames, async (writer, token) =>
				{
					byte[] Picture(long sourceFrame)
					{
						if (!freeBuffers.TryDequeue(out byte[]? pixels)) pixels = new byte[bufferSize];
						if (backdrop is not null) backdrop.DrawInto(pixels, width, height, sourceFrame / fps, derived);
						else MemoryMarshal.Cast<byte, uint>(pixels.AsSpan(0, width * height * 4)).Fill(0xFF000000);
						return pixels;
					}

					async Task Send(byte[] pixels, int outputFrame)
					{
						renderer.RenderOnto(FrameInterpolation.At(shown, outputFrame / fps), pixels, width, height);
						await writer.WriteAsync(pixels, token);
					}

					// The same walk over the kept pieces as the 360 render's (RenderJob.ProduceComposedAsync), the picture drawn from the track's own time.
					int outputFrame = 0;
					byte[]? scratch = null;
					for (int p = 0; p < plan.Pieces.Count && !token.IsCancellationRequested; p++)
					{
						RenderPiece piece = plan.Pieces[p];
						RenderPiece? next = p + 1 < plan.Pieces.Count ? plan.Pieces[p + 1] : null;
						int overlapOut = next?.OverlapIn ?? 0;
						for (long k = piece.OverlapIn; k < piece.FrameCount - overlapOut && !token.IsCancellationRequested; k++, outputFrame++)
						{
							byte[] pixels = Picture(piece.SourceStartFrame + k);
							(double fade, bool white) = CutTransitionFade.At(piece, k, fps);
							CutTransitionFade.Apply(pixels, fade, white);
							await Send(pixels, outputFrame);
						}

						for (int i = 0; i < overlapOut && !token.IsCancellationRequested; i++, outputFrame++)
						{
							byte[] earlier = Picture(piece.SourceEndFrame - overlapOut + i);
							byte[] later = Picture(next!.SourceStartFrame + i);
							scratch ??= new byte[earlier.Length];
							CutTransitionBlend.Blend(earlier, later, scratch, width, height, next.TransitionIn!.Kind, (double)i / overlapOut);
							freeBuffers.Enqueue(later);
							await Send(earlier, outputFrame);
						}
					}
				}, (pixels, count) =>
				{
					freeBuffers.Enqueue(pixels);
					rate.Add(count);
					if (count % 60 == 0)
						Report(RenderPhase.Rendering, string.Format(CoreStrings.Render_Frame, count, totalFrames) + rate.Describe(count, totalFrames), count, totalFrames, RenderProgressKeys.Frames);
				}, ct);

				if (feed.Cancelled) return new RenderResult(false, CoreStrings.Render_Cancelled, sw.Elapsed);

				if (feed.ExitCode != 0)
				{
					AppLogger.Error(string.Format(CoreStrings.Render_FfmpegFailed, feed.ExitCode, feed.StandardError));
					return new RenderResult(false, string.Format(CoreStrings.Render_FfmpegFailedSummary, feed.ExitCode, RenderJob.FfmpegErrorSummary(feed.StandardError)), sw.Elapsed);
				}

				succeeded = true;
				return new RenderResult(true, null, sw.Elapsed);
			}
			finally
			{
				RenderJob.KillFfmpegIfRunning(ffmpeg);
				if (!succeeded && outputIsOurs) RenderJob.DeleteIncompleteOutput(ffmpeg, options.OutputPath);
			}
		}
		catch (OperationCanceledException)
		{
			return new RenderResult(false, CoreStrings.Render_Cancelled, sw.Elapsed);
		}
		catch (Exception ex)
		{
			AppLogger.Error(ex, string.Format(CoreStrings.Render_Failed, options.OutputPath, ex.Message));
			return new RenderResult(false, ex.Message, sw.Elapsed);
		}
	}

	private static IReadOnlyList<OverlayElement> ActiveLayout()
	{
		(List<OverlayPreset> presets, string activeId) = OverlayPresetStore.Load();
		return presets.First(p => p.Id == activeId).Elements;
	}
}
