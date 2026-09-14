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

	public static Process StartRender(string inputPath, string outputPath, SourceInfo info, string encoder,
		bool overwrite, double? limitSeconds = null)
	{
		var (num, den) = ParseFrameRate(info.Video.FrameRate);

		var args = new List<string> { "-hide_banner", "-y" };
		if (!overwrite) args[^1] = "-n";

		if (limitSeconds is not null)
			args.AddRange(["-t", limitSeconds.Value.ToString(CultureInfo.InvariantCulture)]);
		args.AddRange(["-i", inputPath]);

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
				"-cq", "18",
				"-b:v", $"{info.Video.BitRate}",
				"-maxrate", $"{(long)(info.Video.BitRate * 1.2)}",
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
			"-color_range", info.Video.ColorRange ?? "tv"
		]);

		args.Add(outputPath);

		var psi = new ProcessStartInfo("ffmpeg")
		{
			RedirectStandardInput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden
		};
		foreach (var a in args) psi.ArgumentList.Add(a);

		return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
	}

	private static (int num, int den) ParseFrameRate(string rFrameRate)
	{
		var parts = rFrameRate.Split('/');
		return (int.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 1);
	}
}
