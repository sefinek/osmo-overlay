using System.Diagnostics;
using System.Globalization;
using System.Text;
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

	private static bool _nvencConfirmed;

	/// <summary>
	///     Probes with the same 10-bit Main10 output the render uses - some GPUs (e.g. Maxwell GM204) have HEVC
	///     NVENC but no 10-bit support, and an 8-bit probe would pass there only for the real render to fail.
	///     Only a success is remembered: a failure can be transient (consumer cards cap concurrent NVENC
	///     sessions, so another app encoding at the same time makes the probe fail), worth re-probing next time.
	/// </summary>
	public static string SelectVideoEncoder()
	{
		if (_nvencConfirmed) return "hevc_nvenc";

		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffmpeg",
			"-hide_banner", "-loglevel", "error",
			"-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.1",
			"-c:v", "hevc_nvenc",
			"-profile:v", "main10",
			"-pix_fmt", "yuv420p10le",
			"-f", "null", "-");

		_nvencConfirmed = ProcessHelper.RunCaptured(psi).ExitCode == 0;
		return _nvencConfirmed ? "hevc_nvenc" : "libx265";
	}

	public static Process StartRender(IReadOnlyList<VideoSegment> segments, string outputPath, string encoder, bool overwrite,
		RenderEncodeSettings encode, RenderPlan plan, bool greenScreen = false)
	{
		SourceInfo info = segments[0].Source;
		var (num, den) = ParseFrameRate(info.Video.FrameRate);

		var args = new List<string> { "-hide_banner", "-y" };
		if (!overwrite) args[^1] = "-n";

		List<string> tempFiles = [];
		SourceInputs source;
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
			source = new SourceInputs(1, "[0:v]", null, info);
		}
		else
		{
			try
			{
				source = plan.Pieces.Count == 1
					? AddSourceInputs(args, segments, plan.Pieces[0], encode, num, den, tempFiles)
					: AddCutInputs(args, segments, plan, encode, num, den, tempFiles);
			}
			catch
			{
				DeleteTempFiles(tempFiles);
				throw;
			}
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
			$"{source.MainVideo}setparams=color_primaries={primaries}:color_trc={transfer}:colorspace={colorspace}:range={range}[main];" +
			$"[{source.InputCount}:v]scale=out_color_matrix={overlayMatrix}:out_range={range},format=yuva420p10le[ovl];" +
			// shortest=1: the overlay pipe is sized from the container duration, which runs a few ms past the
			// last video frame (audio ends later) - without it overlay's default eof_action=repeat padded the
			// output with copies of the source's last frame (1 extra frame on a 20 s clip, 3 on a 25 min one).
			"[main][ovl]overlay=format=yuv420p10:shortest=1[v]"
		]);

		args.AddRange(["-map", "[v]"]);
		// No corresponding "0:a" input to map when greenScreen replaced input 0 with a silent color
		// source - this export is a compositing asset, not a finished clip, so dropping audio here
		// (rather than muxing it in from a source the user would then have to strip back out) is fine.
		if (source.AudioMap is not null)
		{
			args.AddRange(["-map", source.AudioMap]);
			// Pieces joined by the concat filter are decoded audio - it can't be stream-copied across the
			// joins, so it's re-encoded at the source's own AAC bitrate. A single piece stays a lossless copy.
			args.AddRange(source.EncodeAudio && source.StartSource.Audio is { } audio
				? ["-c:a", "aac", "-b:a", audio.BitRate.ToString(CultureInfo.InvariantCulture)]
				: ["-c:a", "copy"]);
		}

		// Output-side limits: -frames:v because the lavfi color background of a green-screen render is
		// otherwise infinite, -t so a partial render's (copied) audio stops with the picture - the video
		// itself already ends exactly there, the overlay pipe carries exactly FrameCount frames
		// (overlay's shortest=1).
		if (greenScreen)
			args.AddRange(["-frames:v", plan.TotalFrames.ToString(CultureInfo.InvariantCulture)]);
		if (plan.IsPartial)
			args.AddRange(["-t", (plan.TotalFrames * den / (double)num).ToString("R", CultureInfo.InvariantCulture)]);

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
		// For a range render both are moved to the range's own first frame (see SourceInputs.LocalStartFrame).
		if (!greenScreen && source.StartSource.ContainerCreationTimeUtc is { } createdUtc)
		{
			DateTime firstFrameUtc = createdUtc.ToUniversalTime().AddSeconds(source.LocalStartFrame * den / (double)num);
			args.AddRange(["-metadata", $"creation_time={firstFrameUtc:yyyy-MM-ddTHH:mm:ss.ffffffZ}"]);
		}

		if (!greenScreen && source.StartSource.Video.Timecode is { } timecode &&
		    SmpteTimecode.AddFrames(timecode, source.LocalStartFrame, num / (double)den) is { } startTimecode)
			args.AddRange(["-timecode", startTimecode]);
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

		Process process;
		try
		{
			process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
		}
		catch
		{
			DeleteTempFiles(tempFiles);
			throw;
		}

		if (tempFiles.Count > 0)
		{
			process.EnableRaisingEvents = true;
			process.Exited += (_, _) => DeleteTempFiles(tempFiles);
		}

		return process;
	}

	/// <summary>
	///     Adds the source video/audio inputs for a single kept `piece`. From the very start: the file itself, or
	///     all segments through the concat demuxer. From partway in: the segment the range starts in, with a
	///     plain -ss (the one seek verified frame-accurate - see ConcatListWriter for why concat's own
	///     seek can't be used for video); if the range runs on into later segments, those follow as a
	///     concat input from their own beginnings, joined by the concat filter, and the audio comes from
	///     an audio-only concat list (ConcatListWriter.WriteAudioOnly). Verified on real Osmo recordings:
	///     the output's first frame is exactly the range's first frame, the segment seam neither drops
	///     nor repeats a frame, and the audio is in sync (0 ms against a single-file cut of the same span).
	/// </summary>
	private static SourceInputs AddSourceInputs(List<string> args, IReadOnlyList<VideoSegment> segments,
		RenderPiece piece, RenderEncodeSettings encode, int num, int den, List<string> tempFiles)
	{
		void AddHwDecode()
		{
			if (encode.HardwareDecoding) args.AddRange(HwDecodeArgs);
		}

		string? AudioMapFor(int input, SourceInfo s)
		{
			return s.Audio is null ? null : $"{input}:a";
		}

		if (piece.SourceStartFrame == 0)
		{
			AddHwDecode();
			if (segments.Count == 1)
			{
				args.AddRange(["-i", segments[0].InputPath]);
			}
			else
			{
				var listPath = ConcatListWriter.Write(segments.Select(s => s.InputPath));
				tempFiles.Add(listPath);
				args.AddRange(["-f", "concat", "-safe", "0", "-i", listPath]);
			}

			return new SourceInputs(1, "[0:v]", AudioMapFor(0, segments[0].Source), segments[0].Source);
		}

		var (first, localStartFrame) = VideoSegments.Locate(segments, piece.SourceStartFrame);
		var (last, _) = VideoSegments.Locate(segments, piece.SourceEndFrame - 1);
		SourceInfo startSource = segments[first].Source;
		var localStartSeconds = localStartFrame * den / (double)num;
		var seek = localStartSeconds.ToString("R", CultureInfo.InvariantCulture);

		AddHwDecode();
		args.AddRange(["-ss", seek, "-i", segments[first].InputPath]);
		if (last == first)
			return new SourceInputs(1, "[0:v]", AudioMapFor(0, startSource), startSource, localStartFrame);

		List<string> tailPaths = [.. segments.Skip(first + 1).Take(last - first).Select(s => s.InputPath)];
		var tailList = ConcatListWriter.Write(tailPaths);
		tempFiles.Add(tailList);
		AddHwDecode();
		args.AddRange(["-f", "concat", "-safe", "0", "-i", tailList]);
		const string mainVideo = "[0:v][1:v]concat=n=2:v=1:a=0,";

		if (startSource.Audio is null) return new SourceInputs(2, mainVideo, null, startSource, localStartFrame);
		if (startSource.Audio.StreamId is not { } audioStreamId)
			throw new InvalidOperationException("Can't locate the audio track's id in the source, needed to render a range spanning several files.");

		var audioList = ConcatListWriter.WriteAudioOnly([segments[first].InputPath, .. tailPaths], audioStreamId, localStartSeconds);
		tempFiles.Add(audioList);
		args.AddRange(["-itsoffset", SourceProbe.ProbeConcatStartTime(audioList), "-f", "concat", "-safe", "0", "-i", audioList]);
		return new SourceInputs(3, mainVideo, "2:a", startSource, localStartFrame);
	}

	/// <summary>
	///     Inputs for a plan with parts cut out of the middle. Each kept piece is opened on its own the way
	///     AddSourceInputs opens a single one (a plain -ss into the segment it starts in, plus a concat of the
	///     later segments it runs into), trimmed to exactly its frame count, and the pieces are joined by the
	///     concat filter - video and audio together, so they stay in sync across every join.
	/// </summary>
	private static SourceInputs AddCutInputs(List<string> args, IReadOnlyList<VideoSegment> segments, RenderPlan plan,
		RenderEncodeSettings encode, int num, int den, List<string> tempFiles)
	{
		var hasAudio = segments[0].Source.Audio is not null;
		var graph = new StringBuilder();
		var joined = new StringBuilder();
		var input = 0;
		SourceInfo? startSource = null;
		long startLocalFrame = 0;

		for (var p = 0; p < plan.Pieces.Count; p++)
		{
			RenderPiece piece = plan.Pieces[p];
			var (first, localStartFrame) = VideoSegments.Locate(segments, piece.SourceStartFrame);
			var (last, _) = VideoSegments.Locate(segments, piece.SourceEndFrame - 1);
			if (p == 0)
			{
				startSource = segments[first].Source;
				startLocalFrame = localStartFrame;
			}

			if (encode.HardwareDecoding) args.AddRange(HwDecodeArgs);
			args.AddRange(["-ss", (localStartFrame * den / (double)num).ToString("R", CultureInfo.InvariantCulture), "-i", segments[first].InputPath]);
			var head = input++;
			var video = $"[{head}:v]";
			var audio = $"[{head}:a]";

			if (last > first)
			{
				var tailList = ConcatListWriter.Write(segments.Skip(first + 1).Take(last - first).Select(s => s.InputPath));
				tempFiles.Add(tailList);
				if (encode.HardwareDecoding) args.AddRange(HwDecodeArgs);
				args.AddRange(["-f", "concat", "-safe", "0", "-i", tailList]);
				var tail = input++;
				video = $"[{head}:v][{tail}:v]concat=n=2:v=1:a=0,";
				audio = $"[{head}:a][{tail}:a]concat=n=2:v=0:a=1,";
			}

			var pieceSeconds = (piece.FrameCount * den / (double)num).ToString("R", CultureInfo.InvariantCulture);
			graph.Append($"{video}trim=end_frame={piece.FrameCount},setpts=PTS-STARTPTS[p{p}v];");
			joined.Append($"[p{p}v]");
			if (!hasAudio) continue;

			graph.Append($"{audio}atrim=end={pieceSeconds},asetpts=PTS-STARTPTS[p{p}a];");
			joined.Append($"[p{p}a]");
		}

		// Timestamps rebuilt from the frame number after the join: concat's own come out a hair off the exact
		// frame grid, and overlay's shortest=1 then dropped the final frame (measured: 898 of 899) because
		// it sat just past the overlay pipe's last timestamp. The pipe's are exactly N * den / num.
		graph.Append($"{joined}concat=n={plan.Pieces.Count}:v=1:a={(hasAudio ? 1 : 0)}[cutv]{(hasAudio ? "[cuta]" : "")};" +
		             $"[cutv]setpts=N*{den}/{num}/TB,");
		return new SourceInputs(input, graph.ToString(), hasAudio ? "[cuta]" : null, startSource!, startLocalFrame, true);
	}

	private static void DeleteTempFiles(List<string> paths)
	{
		foreach (var path in paths)
			try
			{
				File.Delete(path);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Best-effort: a stray temp file is harmless, not worth failing over.
			}
	}

	/// <summary>Clears concat lists left in the temp folder by earlier runs that didn't exit cleanly - see ConcatListWriter.DeleteStale.</summary>
	public static int DeleteStaleTempFiles()
	{
		return ConcatListWriter.DeleteStale(TimeSpan.FromDays(1));
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

	/// <summary>
	///     What AddSourceInputs/AddCutInputs set up: how many inputs precede the overlay pipe, the filter-graph source
	///     of the main video ("[0:v]" or a concat of two inputs), the -map for audio (null for none), and
	///     the segment the output starts in plus how many frames into it (for creation_time/timecode).
	/// </summary>
	private sealed record SourceInputs(int InputCount, string MainVideo, string? AudioMap, SourceInfo StartSource, long LocalStartFrame = 0,
		bool EncodeAudio = false);
}

/// <summary>
///     Export options on top of the source-matched defaults (see OverlaySettings): NVENC preset,
///     hardware decoding, bitrate relative to the source, and ffmpeg-side fast start.
/// </summary>
public sealed record RenderEncodeSettings(string NvencPreset, bool HardwareDecoding, double BitrateMultiplier, bool FfmpegFastStart);
