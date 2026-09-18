using System.Diagnostics;
using System.Globalization;

namespace OsmoOverlay.Core.Ffmpeg;

public static class FfmpegPipeline
{
	public static string SelectVideoEncoder()
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffmpeg",
			"-hide_banner", "-loglevel", "error",
			"-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.1",
			"-c:v", "hevc_nvenc",
			"-f", "null", "-");

		using Process process = Process.Start(psi)!;
		process.WaitForExit();

		return process.ExitCode == 0 ? "hevc_nvenc" : "libx265";
	}

	public static Process StartRender(IReadOnlyList<string> inputPaths, string outputPath, SourceInfo info,
		string encoder, bool overwrite, double? limitSeconds = null)
	{
		var (num, den) = ParseFrameRate(info.Video.FrameRate);

		var args = new List<string> { "-hide_banner", "-y" };
		if (!overwrite) args[^1] = "-n";

		if (limitSeconds is not null)
			args.AddRange(["-t", limitSeconds.Value.ToString(CultureInfo.InvariantCulture)]);

		// A single file is fed directly, exactly as before - the concat demuxer only kicks in for
		// stitched multi-segment recordings, so the common single-file path has zero behavior change.
		string? concatListPath = null;
		if (inputPaths.Count == 1)
		{
			args.AddRange(["-i", inputPaths[0]]);
		}
		else
		{
			concatListPath = ConcatListWriter.Write(inputPaths.Select(p => (p, (double?)null)));
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

		args.AddRange([
			"-filter_complex",
			"[1:v]format=yuva420p10le[ovl];" +
			"[0:v][ovl]overlay=format=yuv420p10[ov2];" +
			$"[ov2]setparams=color_primaries={primaries}:color_trc={transfer}:colorspace={colorspace}:range={range}[v]"
		]);

		args.AddRange(["-map", "[v]"]);
		if (info.Audio is not null)
			args.AddRange(["-map", "0:a", "-c:a", "copy"]);

		if (encoder == "hevc_nvenc")
			args.AddRange([
				"-c:v", "hevc_nvenc",
				"-preset", "p7",
				"-rc", "vbr",
				// b:v = source's own bitrate is the actual target (parity with the source, not an
				// independent quality knob) - measured: adding -cq on top fights the b:v target and
				// let the encoder roughly double the source bitrate on high-motion footage, which is
				// its own kind of "diverges from the source" even though nothing looked worse. maxrate/
				// bufsize give VBR a little headroom over the bare average to borrow bits for a complex
				// frame from a simpler one nearby - kept modest (not the 3x tried initially) because a
				// wide bufsize forces NVENC to signal a higher HEVC level than the source needs, which
				// is exactly the kind of divergence to avoid; measured close to the source's own level
				// at this size.
				"-b:v", $"{info.Video.BitRate}",
				"-maxrate", $"{(long)(info.Video.BitRate * 1.2)}",
				"-bufsize", $"{info.Video.BitRate * 2}",
				"-profile:v", "main10",
				"-pix_fmt", "yuv420p10le"
			]);
		else
			args.AddRange([
				"-c:v", "libx265",
				"-preset", "slow",
				"-crf", "16",
				"-pix_fmt", "yuv420p10le"
			]);

		args.AddRange([
			"-color_primaries", info.Video.ColorPrimaries ?? "bt709",
			"-color_trc", info.Video.ColorTransfer ?? "bt709",
			"-colorspace", info.Video.ColorSpace ?? "bt709",
			"-color_range", info.Video.ColorRange ?? "tv",
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

	private static (int num, int den) ParseFrameRate(string rFrameRate)
	{
		var parts = rFrameRate.Split('/');
		return (int.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 1);
	}
}
