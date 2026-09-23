using System.Diagnostics;
using System.Globalization;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Core.Ffmpeg;

public static class FfmpegPipeline
{
	// HSV (115 degrees, 0.9, 0.96), rounded to 8-bit RGB (43, 245, 24).
	private const string GreenScreenColor = "0x2BF518";

	// Hardware decode of the source. Measured on a real Osmo Action 6 4K60 10-bit HEVC file (~73 Mbps):
	// software decode alone tops out at ~38 fps and was the single slowest stage of the whole render;
	// hardware decode (even with the copy back to system memory the CPU overlay filter needs) runs at
	// ~150 fps, taking a full render from ~27 to ~39 fps. "auto" rather than a specific API, so a machine
	// without a usable decoder just falls back to software instead of failing the render - same choice
	// the live preview makes (VideoFrameSource).
	private static readonly string[] HwDecodeArgs = ["-hwaccel", "auto"];

	public static string SelectVideoEncoder()
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffmpeg",
			"-hide_banner", "-loglevel", "error",
			"-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.1",
			"-c:v", "hevc_nvenc",
			"-f", "null", "-");

		return ProcessHelper.RunCaptured(psi).ExitCode == 0 ? "hevc_nvenc" : "libx265";
	}

	public static Process StartRender(IReadOnlyList<string> inputPaths, string outputPath, SourceInfo info,
		string encoder, bool overwrite, RenderEncodeSettings encode, double? limitSeconds = null, bool greenScreen = false,
		long totalFrames = 0)
	{
		var (num, den) = ParseFrameRate(info.Video.FrameRate);

		var args = new List<string> { "-hide_banner", "-y" };
		if (!overwrite) args[^1] = "-n";

		if (limitSeconds is not null)
			args.AddRange(["-t", limitSeconds.Value.ToString(CultureInfo.InvariantCulture)]);

		// A single file is fed directly, exactly as before - the concat demuxer only kicks in for
		// stitched multi-segment recordings, so the common single-file path has zero behavior change.
		string? concatListPath = null;
		if (greenScreen)
		{
			// A synthetic solid-color background instead of decoding/re-muxing the real source - the
			// whole point of this mode is an alpha-free HUD-only asset for compositing elsewhere, so
			// there's nothing about the source video itself worth preserving (and skipping its decode
			// makes this render lighter too). Left otherwise-infinite here (no per-input frame limit -
			// -frames:v is an output-only option in ffmpeg, it belongs down by -map [v] below, not here)
			// unlike the real source video, which ends the filtergraph/output naturally at its own
			// duration.
			args.AddRange([
				"-f", "lavfi", "-i", $"color=c={GreenScreenColor}:s={info.Video.Width}x{info.Video.Height}:r={num}/{den}"
			]);
		}
		else if (inputPaths.Count == 1)
		{
			if (encode.HardwareDecoding) args.AddRange(HwDecodeArgs);
			args.AddRange(["-i", inputPaths[0]]);
		}
		else
		{
			concatListPath = ConcatListWriter.Write(inputPaths);
			if (encode.HardwareDecoding) args.AddRange(HwDecodeArgs);
			args.AddRange(["-f", "concat", "-safe", "0", "-i", concatListPath]);
		}

		args.AddRange([
			"-f", "rawvideo",
			"-pix_fmt", "bgra",
			"-s", $"{info.Video.Width}x{info.Video.Height}",
			"-r", $"{num}/{den}",
			"-i", "pipe:0"
		]);

		var primaries = info.Video.ColorPrimaries ?? "bt709";
		var transfer = info.Video.ColorTransfer ?? "bt709";
		var colorspace = info.Video.ColorSpace ?? "bt709";
		var range = (info.Video.ColorRange ?? "tv") == "pc" ? "pc" : "tv";

		// The overlay's RGB -> YUV conversion must use the same matrix the output is tagged with, or the HUD's
		// colors come out shifted. Explicit on the scaler because older ffmpeg's swscale defaults to BT.601;
		// and the main input is tagged *before* the overlay (not only the output after it) because newer
		// ffmpeg negotiates the overlay input's colorspace to match the main input's - an untagged source
		// (e.g. an NLE export missing its color tags, see ColorTagFixer) would otherwise drag the HUD back
		// to BT.601 while the output still gets tagged as BT.709.
		var overlayMatrix = colorspace switch
		{
			"bt2020nc" or "bt2020c" => "bt2020",
			"smpte170m" or "bt470bg" => "bt601",
			"smpte240m" => "smpte240m",
			"fcc" => "fcc",
			_ => "bt709"
		};

		args.AddRange([
			"-filter_complex",
			$"[0:v]setparams=color_primaries={primaries}:color_trc={transfer}:colorspace={colorspace}:range={range}[main];" +
			$"[1:v]scale=out_color_matrix={overlayMatrix}:out_range={range},format=yuva420p10le[ovl];" +
			// shortest=1: the overlay pipe is sized from the container duration, which runs a few ms past the
			// last video frame (audio ends later) - without it overlay's default eof_action=repeat padded the
			// output with copies of the source's last frame (1 extra frame on a 20 s clip, 3 on a 25 min one).
			"[main][ovl]overlay=format=yuv420p10:shortest=1[v]"
		]);

		args.AddRange(["-map", "[v]"]);
		// No corresponding "0:a" input to map when greenScreen replaced input 0 with a silent color
		// source - this export is a compositing asset, not a finished clip, so dropping audio here
		// (rather than muxing it in from a source the user would then have to strip back out) is fine.
		if (!greenScreen && info.Audio is not null)
			args.AddRange(["-map", "0:a", "-c:a", "copy"]);

		// Output-side limit (unlike -t above, -frames:v is only valid as an output option) on the [v]
		// stream - needed because the lavfi color background feeding it is otherwise infinite, unlike
		// the real source video's own natural duration the non-green-screen path relies on instead.
		if (greenScreen)
			args.AddRange(["-frames:v", totalFrames.ToString(CultureInfo.InvariantCulture)]);

		// Reproduce the camera's own encode as closely as the encoder allows, not just its codec/profile:
		// measured on Osmo Action 6 files, the camera writes near-constant bitrate (within ~5-10% of its
		// target every second), no B-frames, a fixed 1 s GOP and HEVC level 5.2. Constant bitrate at the
		// source's own rate (1 s buffer) instead of VBR: VBR spent ~3 Mbps on the static route-intro card
		// and then ran over the source's rate for the rest, so neither the average nor the per-second
		// rate matched the original.
		var bitRate = ((long)Math.Round(info.Video.BitRate * encode.BitrateMultiplier)).ToString(CultureInfo.InvariantCulture);
		var gop = info.Video.KeyframeIntervalFrames ?? (int)Math.Round(num / (double)den);
		if (encoder == "hevc_nvenc")
		{
			args.AddRange([
				"-c:v", "hevc_nvenc",
				"-preset", encode.NvencPreset,
				"-rc", "cbr",
				"-b:v", bitRate,
				"-bufsize", bitRate,
				"-bf", "0",
				"-g", gop.ToString(CultureInfo.InvariantCulture),
				"-profile:v", "main10",
				"-pix_fmt", "yuv420p10le"
			]);
			if (info.Video.Level > 0) args.AddRange(["-level", info.Video.Level.ToString(CultureInfo.InvariantCulture)]);
			if (info.Video.HighTier is { } highTier) args.AddRange(["-tier", highTier ? "high" : "main"]);
		}
		else
		{
			List<string> x265Params = ["bframes=0", $"keyint={gop}", $"min-keyint={gop}", "scenecut=0"];
			if (info.Video.Level > 0)
				x265Params.Add($"level-idc={(info.Video.Level / 30.0).ToString("0.#", CultureInfo.InvariantCulture)}");
			if (info.Video.HighTier is { } highTier) x265Params.Add(highTier ? "high-tier=1" : "high-tier=0");

			args.AddRange([
				"-c:v", "libx265",
				"-preset", "slow",
				"-b:v", bitRate,
				"-maxrate", bitRate,
				"-bufsize", bitRate,
				"-x265-params", string.Join(':', x265Params),
				"-pix_fmt", "yuv420p10le"
			]);
		}

		// Container-level metadata the camera wrote: recording date (file managers, photo libraries and
		// NLEs sort by it - without it the render looks like it was shot at render time) and the start
		// timecode (lets an NLE line the render up against the original). Not the djmd/dbgi streams:
		// djmd carries the GPS track and the camera's serial number, which don't belong in a video that's
		// meant to be shared.
		if (!greenScreen && info.ContainerCreationTimeUtc is { } createdUtc)
			args.AddRange(["-metadata", $"creation_time={createdUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.ffffffZ}"]);
		if (!greenScreen && info.Video.Timecode is { } timecode)
			args.AddRange(["-timecode", timecode]);
		if (encode.FfmpegFastStart)
			args.AddRange(["-movflags", "+faststart"]);

		args.AddRange([
			"-color_primaries", primaries,
			"-color_trc", transfer,
			"-colorspace", colorspace,
			"-color_range", range,
			// Matches the source's MP4 HEVC tag (DJI writes hvc1: SPS/PPS/VPS out-of-band) instead of
			// ffmpeg's hev1 default, so pickier players/editors (DaVinci Resolve, older QuickTime/FCP)
			// that expect hvc1 don't choke on an otherwise-identical bitstream.
			"-tag:v", "hvc1"
		]);

		args.Add(outputPath);

		ProcessStartInfo psi = ProcessHelper.CreateHiddenWithStdin("ffmpeg", args);

		Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");

		if (concatListPath is not null)
		{
			process.EnableRaisingEvents = true;
			process.Exited += (_, _) =>
			{
				try
				{
					File.Delete(concatListPath);
				}
				catch
				{
					// Best-effort: a stray temp file is harmless, not worth failing over.
				}
			};
		}

		return process;
	}

	/// <summary>Maps OverlaySettings' export options, see there for what each default preserves.</summary>
	public static RenderEncodeSettings EncodeSettingsFrom(OverlaySettings settings, bool postProcessing)
	{
		string[] presets = ["p1", "p2", "p3", "p4", "p5", "p6", "p7"];
		return new RenderEncodeSettings(
			presets.Contains(settings.NvencPreset) ? settings.NvencPreset : "p7",
			settings.HardwareDecoding,
			settings.OutputBitrateMultiplier is >= 0.5 and <= 4 ? settings.OutputBitrateMultiplier : 1.0,
			// ffmpeg's own +faststart only when nothing edits the file after ffmpeg - otherwise
			// Mp4FastStart runs last instead (ffmpeg's would just be undone by the append).
			settings.FastStart && !postProcessing);
	}

	private static (int num, int den) ParseFrameRate(string rFrameRate)
	{
		var parts = rFrameRate.Split('/');
		return (int.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 1);
	}
}

/// <summary>
///     Export options on top of the source-matched defaults (see OverlaySettings): NVENC preset,
///     hardware decoding, bitrate relative to the source, and ffmpeg-side fast start.
/// </summary>
public sealed record RenderEncodeSettings(string NvencPreset, bool HardwareDecoding, double BitrateMultiplier, bool FfmpegFastStart);
