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
	// the live preview makes (LibavStreamDecoder).
	private static readonly string[] HwDecodeArgs = ["-hwaccel", "auto"];

	private static readonly HashSet<(string Encoder, bool TenBit)> NvencConfirmed = [];

	/// <summary>
	///     The encoder for the source's own codec - H.264 stays H.264 (an Insta360 Studio export), everything else is
	///     encoded as HEVC like the Osmo's own files: NVENC when this GPU can, else x264/x265. Probed with the render's
	///     profile and bit depth - some GPUs (e.g. Maxwell GM204) have HEVC NVENC but no 10-bit support, and an 8-bit
	///     probe would pass there only for the real render to fail. Only a success is remembered: a failure can be
	///     transient (consumer cards cap concurrent NVENC sessions, so another app encoding at the same time makes the
	///     probe fail), worth re-probing next time.
	/// </summary>
	public static string SelectVideoEncoder(VideoInfo source)
	{
		bool h264 = IsH264(source);
		bool tenBit = IsTenBit(source);
		string nvenc = h264 ? "h264_nvenc" : "hevc_nvenc";
		lock (NvencConfirmed)
		{
			if (NvencConfirmed.Contains((nvenc, tenBit)))
				return nvenc;
		}

		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffmpeg",
			"-hide_banner", "-loglevel", "error",
			"-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.1",
			"-c:v", nvenc,
			"-profile:v", Profile(h264, tenBit),
			"-pix_fmt", PixelFormat(tenBit),
			"-f", "null", "-");

		if (ProcessHelper.RunCaptured(psi).ExitCode != 0) return h264 ? "libx264" : "libx265";

		lock (NvencConfirmed) NvencConfirmed.Add((nvenc, tenBit));

		return nvenc;
	}

	public static bool IsGpuEncoder(string encoder)
	{
		return encoder.EndsWith("_nvenc", StringComparison.Ordinal);
	}

	private static bool IsH264(VideoInfo video)
	{
		return video.CodecName == "h264";
	}

	private static bool IsTenBit(VideoInfo video)
	{
		return video.PixFmt.Contains("10", StringComparison.Ordinal);
	}

	private static string Profile(bool h264, bool tenBit)
	{
		return h264 ? tenBit ? "high10" : "high" : tenBit ? "main10" : "main";
	}

	private static string PixelFormat(bool tenBit)
	{
		return tenBit ? "yuv420p10le" : "yuv420p";
	}

	public static Process StartRender(IReadOnlyList<VideoSegment> segments, string outputPath, string encoder, bool overwrite,
		RenderEncodeSettings encode, RenderPlan plan, bool greenScreen = false, bool composedPicture = false)
	{
		SourceInfo info = segments[0].Source;
		(int num, int den) = ParseFrameRate(info.Video.FrameRate);

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
					? AddSourceInputs(args, segments, plan.Pieces[0], encode, num, den, tempFiles, !composedPicture)
					: AddCutInputs(args, segments, plan, encode, num, den, tempFiles, !composedPicture);
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

		string primaries = info.Video.ColorPrimaries ?? "bt709";
		string transfer = info.Video.ColorTransfer ?? "bt709";
		string colorspace = info.Video.ColorSpace ?? "bt709";
		string range = (info.Video.ColorRange ?? "tv") == "pc" ? "pc" : "tv";
		bool h264 = encoder is "h264_nvenc" or "libx264";
		bool tenBit = IsTenBit(info.Video);

		// The overlay's RGB -> YUV conversion must use the same matrix the output is tagged with, or the HUD's
		// colors come out shifted. Explicit on the scaler because older ffmpeg's swscale defaults to BT.601;
		// and the main input is tagged *before* the overlay (not only the output after it) because newer
		// ffmpeg negotiates the overlay input's colorspace to match the main input's - an untagged source
		// (e.g. an NLE export missing its color tags, see ColorTagFixer) would otherwise drag the HUD back
		// to BT.601 while the output still gets tagged as BT.709.
		string overlayMatrix = colorspace switch
		{
			"bt2020nc" or "bt2020c" => "bt2020",
			"smpte170m" or "bt470bg" => "bt601",
			"smpte240m" => "smpte240m",
			"fcc" => "fcc",
			_ => "bt709"
		};

		string tags = $"setparams=color_primaries={primaries}:color_trc={transfer}:colorspace={colorspace}:range={range}";
		args.AddRange([
			"-filter_complex",
			composedPicture
				// The pipe already carries the finished picture (a 360 recording's view with the overlay on it) - it's
				// only turned into YUV with the output's own matrix; the inputs before it are there for the sound.
				? $"{source.MainVideo}[{source.InputCount}:v]scale=out_color_matrix={overlayMatrix}:out_range={range}," +
				  $"format={(tenBit ? "yuv420p10le" : "yuv420p")},{tags}[v]"
				: $"{source.MainVideo}{tags}[main];" +
				  $"[{source.InputCount}:v]scale=out_color_matrix={overlayMatrix}:out_range={range},format={(tenBit ? "yuva420p10le" : "yuva420p")}[ovl];" +
				  // shortest=1: the overlay pipe is sized from the container duration, which runs a few ms past the
				  // last video frame (audio ends later) - without it overlay's default eof_action=repeat padded the
				  // output with copies of the source's last frame (1 extra frame on a 20 s clip, 3 on a 25 min one).
				  $"[main][ovl]overlay=format={(tenBit ? "yuv420p10" : "yuv420")}:shortest=1[v]"
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
			if (!source.EncodeAudio)
			{
				args.AddRange(["-c:a", "copy"]);
			}
			else
			{
				args.AddRange(["-c:a", "aac"]);
				// 0 = the source's rate couldn't be read - ffmpeg's default then, rather than asking for 0 b/s.
				if (source.StartSource.Audio is { BitRate: > 0 } audio) args.AddRange(["-b:a", audio.BitRate.ToString(CultureInfo.InvariantCulture)]);
			}
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
		string bitRate = ((long)Math.Round(info.Video.BitRate * encode.BitrateMultiplier)).ToString(CultureInfo.InvariantCulture);
		int gop = info.Video.KeyframeIntervalFrames ?? (int)Math.Round(num / (double)den);
		if (IsGpuEncoder(encoder))
		{
			args.AddRange([
				"-c:v", encoder,
				"-preset", encode.NvencPreset,
				"-rc", "cbr",
				"-b:v", bitRate,
				"-bufsize", bitRate,
				"-bf", "0",
				"-g", gop.ToString(CultureInfo.InvariantCulture),
				"-profile:v", Profile(h264, tenBit),
				"-pix_fmt", PixelFormat(tenBit)
			]);
			// ffprobe's level is NVENC's own number for both codecs (HEVC 5.2 = 156, H.264 5.1 = 51).
			if (info.Video.Level > 0) args.AddRange(["-level", info.Video.Level.ToString(CultureInfo.InvariantCulture)]);
			if (!h264 && info.Video.HighTier is { } highTier) args.AddRange(["-tier", highTier ? "high" : "main"]);
		}
		else if (h264)
		{
			List<string> x264Params = ["bframes=0", $"keyint={gop}", $"min-keyint={gop}", "scenecut=0"];
			args.AddRange([
				"-c:v", "libx264",
				"-preset", "slow",
				"-b:v", bitRate,
				"-maxrate", bitRate,
				"-bufsize", bitRate,
				"-x264-params", string.Join(':', x264Params),
				"-profile:v", Profile(true, tenBit),
				"-pix_fmt", PixelFormat(tenBit)
			]);
			if (info.Video.Level > 0) args.AddRange(["-level", (info.Video.Level / 10.0).ToString("0.#", CultureInfo.InvariantCulture)]);
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
				"-pix_fmt", PixelFormat(tenBit)
			]);
		}

		// Container-level metadata the camera wrote: recording date (file managers, photo libraries and
		// NLEs sort by it - without it the render looks like it was shot at render time) and the start
		// timecode (lets an NLE line the render up against the original). Not the camera's data streams
		// (DJI's djmd/dbgi): they carry the GPS track and the camera's serial number, which don't belong in a video that's
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
			"-color_range", range
		]);
		// Matches the source's MP4 HEVC tag (DJI writes hvc1: SPS/PPS/VPS out-of-band) instead of
		// ffmpeg's hev1 default, so pickier players/editors (DaVinci Resolve, older QuickTime/FCP)
		// that expect hvc1 don't choke on an otherwise-identical bitstream.
		if (!h264) args.AddRange(["-tag:v", "hvc1"]);

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
		RenderPiece piece, RenderEncodeSettings encode, int num, int den, List<string> tempFiles, bool withVideo)
	{
		void AddHwDecode()
		{
			if (withVideo && encode.HardwareDecoding) args.AddRange(HwDecodeArgs);
		}

		string? AudioMapFor(int input, SourceInfo s)
		{
			return s.Audio is null ? null : $"{input}:a";
		}

		string video = withVideo ? "[0:v]" : "";
		if (piece.SourceStartFrame == 0)
		{
			AddHwDecode();
			if (segments.Count == 1)
			{
				args.AddRange(["-i", segments[0].InputPath]);
			}
			else
			{
				string listPath = ConcatListWriter.Write(segments.Select(s => s.InputPath));
				tempFiles.Add(listPath);
				args.AddRange(["-f", "concat", "-safe", "0", "-i", listPath]);
			}

			return new SourceInputs(1, video, AudioMapFor(0, segments[0].Source), segments[0].Source);
		}

		(int first, long localStartFrame) = VideoSegments.Locate(segments, piece.SourceStartFrame);
		(int last, _) = VideoSegments.Locate(segments, piece.SourceEndFrame - 1);
		SourceInfo startSource = segments[first].Source;
		double localStartSeconds = localStartFrame * den / (double)num;
		string seek = localStartSeconds.ToString("R", CultureInfo.InvariantCulture);

		AddHwDecode();
		args.AddRange(["-ss", seek, "-i", segments[first].InputPath]);
		if (last == first)
			return new SourceInputs(1, video, AudioMapFor(0, startSource), startSource, localStartFrame);

		List<string> tailPaths = [.. segments.Skip(first + 1).Take(last - first).Select(s => s.InputPath)];
		int inputs = 1;
		if (withVideo)
		{
			string tailList = ConcatListWriter.Write(tailPaths);
			tempFiles.Add(tailList);
			AddHwDecode();
			args.AddRange(["-f", "concat", "-safe", "0", "-i", tailList]);
			video = "[0:v][1:v]concat=n=2:v=1:a=0,";
			inputs++;
		}

		if (startSource.Audio is null) return new SourceInputs(inputs, video, null, startSource, localStartFrame);
		if (startSource.Audio.StreamId is not { } audioStreamId)
			throw new InvalidOperationException("Can't locate the audio track's id in the source, needed to render a range spanning several files.");

		string audioList = ConcatListWriter.WriteAudioOnly([segments[first].InputPath, .. tailPaths], audioStreamId, localStartSeconds);
		tempFiles.Add(audioList);
		args.AddRange(["-itsoffset", SourceProbe.ProbeConcatStartTime(audioList), "-f", "concat", "-safe", "0", "-i", audioList]);
		return new SourceInputs(inputs + 1, video, $"{inputs}:a", startSource, localStartFrame);
	}

	/// <summary>
	///     Inputs for a plan with parts cut out of the middle. Each kept piece is opened on its own the way
	///     AddSourceInputs opens a single one (a plain -ss into the segment it starts in, plus a concat of the
	///     later segments it runs into), trimmed to exactly its frame count, and the pieces are joined by the
	///     concat filter - video and audio together, so they stay in sync across every join. Without video only the
	///     sound is joined, and the graph ends there.
	/// </summary>
	private static SourceInputs AddCutInputs(List<string> args, IReadOnlyList<VideoSegment> segments, RenderPlan plan,
		RenderEncodeSettings encode, int num, int den, List<string> tempFiles, bool withVideo)
	{
		bool hasAudio = segments[0].Source.Audio is not null;
		var graph = new StringBuilder();
		var joined = new StringBuilder();
		int input = 0;
		SourceInfo? startSource = null;
		long startLocalFrame = 0;

		for (int p = 0; p < plan.Pieces.Count; p++)
		{
			RenderPiece piece = plan.Pieces[p];
			(int first, long localStartFrame) = VideoSegments.Locate(segments, piece.SourceStartFrame);
			(int last, _) = VideoSegments.Locate(segments, piece.SourceEndFrame - 1);
			if (p == 0)
			{
				startSource = segments[first].Source;
				startLocalFrame = localStartFrame;
			}

			if (withVideo && encode.HardwareDecoding) args.AddRange(HwDecodeArgs);
			args.AddRange(["-ss", (localStartFrame * den / (double)num).ToString("R", CultureInfo.InvariantCulture), "-i", segments[first].InputPath]);
			int head = input++;
			string video = $"[{head}:v]";
			string audio = $"[{head}:a]";

			if (last > first)
			{
				string tailList = ConcatListWriter.Write(segments.Skip(first + 1).Take(last - first).Select(s => s.InputPath));
				tempFiles.Add(tailList);
				if (withVideo && encode.HardwareDecoding) args.AddRange(HwDecodeArgs);
				args.AddRange(["-f", "concat", "-safe", "0", "-i", tailList]);
				int tail = input++;
				video = $"[{head}:v][{tail}:v]concat=n=2:v=1:a=0,";
				audio = $"[{head}:a][{tail}:a]concat=n=2:v=0:a=1,";
			}

			if (withVideo)
			{
				graph.Append($"{video}trim=end_frame={piece.FrameCount},setpts=PTS-STARTPTS{VideoFades(piece, num / (double)den)}[p{p}v];");
				joined.Append($"[p{p}v]");
			}

			if (!hasAudio) continue;

			string pieceSeconds = (piece.FrameCount * den / (double)num).ToString("R", CultureInfo.InvariantCulture);
			graph.Append($"{audio}atrim=end={pieceSeconds},asetpts=PTS-STARTPTS{AudioFades(piece, num / (double)den)}[p{p}a];");
			joined.Append($"[p{p}a]");
		}

		// Overlapping transitions (xfade/acrossfade) can't go through one concat: the parts are joined one after another,
		// each by the concat or the transition its cut asks for. Every piece's frames are exact, so the offsets are too.
		if (plan.Pieces.Any(p => p.OverlapIn > 0))
		{
			string Seconds(double frames)
			{
				return (frames * den / num).ToString("R", CultureInfo.InvariantCulture);
			}

			string joinedVideo = "[p0v]", joinedAudio = "[p0a]";
			long outputFrames = plan.Pieces[0].FrameCount;
			for (int p = 1; p < plan.Pieces.Count; p++)
			{
				RenderPiece piece = plan.Pieces[p];
				int overlap = piece.OverlapIn;
				if (withVideo)
				{
					graph.Append(overlap > 0
						? $"{joinedVideo}[p{p}v]xfade=transition={piece.TransitionIn!.XfadeName}:duration={Seconds(overlap)}:offset={Seconds(outputFrames - overlap)}[x{p}v];"
						: $"{joinedVideo}[p{p}v]concat=n=2:v=1:a=0[x{p}v];");
					joinedVideo = $"[x{p}v]";
				}

				if (hasAudio)
				{
					graph.Append(overlap > 0
						? $"{joinedAudio}[p{p}a]acrossfade=d={Seconds(overlap)}:c1=tri:c2=tri[x{p}a];"
						: $"{joinedAudio}[p{p}a]concat=n=2:v=0:a=1[x{p}a];");
					joinedAudio = $"[x{p}a]";
				}

				outputFrames += piece.FrameCount - overlap;
			}

			if (hasAudio) graph.Append($"{joinedAudio}anull[cuta];");
			if (withVideo) graph.Append($"{joinedVideo}setpts=N*{den}/{num}/TB,");
			return new SourceInputs(input, graph.ToString(), hasAudio ? "[cuta]" : null, startSource!, startLocalFrame, true);
		}

		if (!withVideo)
		{
			if (hasAudio) graph.Append($"{joined}concat=n={plan.Pieces.Count}:v=0:a=1[cuta];");
			return new SourceInputs(input, graph.ToString(), hasAudio ? "[cuta]" : null, startSource!, startLocalFrame, true);
		}

		// Timestamps rebuilt from the frame number after the join: concat's own come out a hair off the exact
		// frame grid, and overlay's shortest=1 then dropped the final frame (measured: 898 of 899) because
		// it sat just past the overlay pipe's last timestamp. The pipe's are exactly N * den / num.
		graph.Append($"{joined}concat=n={plan.Pieces.Count}:v=1:a={(hasAudio ? 1 : 0)}[cutv]{(hasAudio ? "[cuta]" : "")};" +
		             $"[cutv]setpts=N*{den}/{num}/TB,");
		return new SourceInputs(input, graph.ToString(), hasAudio ? "[cuta]" : null, startSource!, startLocalFrame, true);
	}

	/// <summary>
	///     A piece's fades for the cuts around it (CutTransition), inside its own frames - the filters that follow the piece's
	///     timestamps being rebuilt. Same curve as CutTransitionFade, which the pictures made in C# use.
	/// </summary>
	private static string VideoFades(RenderPiece piece, double fps)
	{
		var filters = new StringBuilder();
		if (piece.TransitionIn is { Overlaps: false } fadeIn)
			filters.Append($",fade=t=in:s=0:n={fadeIn.HalfFrames(fps, piece.FrameCount)}:color={fadeIn.ColorName}");
		if (piece.TransitionOut is { Overlaps: false } fadeOut)
		{
			int n = fadeOut.HalfFrames(fps, piece.FrameCount);
			filters.Append($",fade=t=out:s={piece.FrameCount - n}:n={n}:color={fadeOut.ColorName}");
		}

		return filters.ToString();
	}

	/// <summary>The sound fades with the picture - to silence either way, a white fade has no sound of its own.</summary>
	private static string AudioFades(RenderPiece piece, double fps)
	{
		string Seconds(double frames)
		{
			return (frames / fps).ToString("R", CultureInfo.InvariantCulture);
		}

		var filters = new StringBuilder();
		if (piece.TransitionIn is { Overlaps: false } fadeIn)
			filters.Append($",afade=t=in:st=0:d={Seconds(fadeIn.HalfFrames(fps, piece.FrameCount))}");
		if (piece.TransitionOut is { Overlaps: false } fadeOut)
		{
			int n = fadeOut.HalfFrames(fps, piece.FrameCount);
			filters.Append($",afade=t=out:st={Seconds(piece.FrameCount - n)}:d={Seconds(n)}");
		}

		return filters.ToString();
	}

	private static void DeleteTempFiles(List<string> paths)
	{
		foreach (string path in paths)
		{
			try
			{
				File.Delete(path);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Best-effort: a stray temp file is harmless, not worth failing over.
			}
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
		string[] parts = rFrameRate.Split('/');
		return (int.Parse(parts[0], CultureInfo.InvariantCulture), parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1);
	}

	/// <summary>
	///     What AddSourceInputs/AddCutInputs set up: how many inputs precede the overlay pipe, the filter-graph source
	///     of the main video ("[0:v]" or a concat of two inputs), the -map for audio (null for none), and
	///     the segment the output starts in plus how many frames into it (for creation_time/timecode).
	/// </summary>
	private sealed record SourceInputs(
		int InputCount,
		string MainVideo,
		string? AudioMap,
		SourceInfo StartSource,
		long LocalStartFrame = 0,
		bool EncodeAudio = false);
}

/// <summary>
///     Export options on top of the source-matched defaults (see OverlaySettings): NVENC preset,
///     hardware decoding, bitrate relative to the source, and ffmpeg-side fast start.
/// </summary>
public sealed record RenderEncodeSettings(string NvencPreset, bool HardwareDecoding, double BitrateMultiplier, bool FfmpegFastStart);
