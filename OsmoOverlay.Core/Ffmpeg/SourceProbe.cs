using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace OsmoOverlay.Core.Ffmpeg;

public sealed record VideoInfo(
	string CodecName,
	string Profile,
	int Width,
	int Height,
	string FrameRate,
	string PixFmt,
	string? ColorPrimaries,
	string? ColorTransfer,
	string? ColorSpace,
	string? ColorRange,
	long BitRate)
{
	public double Fps
	{
		get
		{
			var parts = FrameRate.Split('/');
			return parts.Length > 1
				? double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture)
				: double.Parse(parts[0], CultureInfo.InvariantCulture);
		}
	}
}

public sealed record AudioInfo(string CodecName, int SampleRate, int Channels);

public sealed record SourceInfo(
	VideoInfo Video,
	AudioInfo? Audio,
	bool HasDjmdTrack,
	double DurationSeconds,
	int? DjmdStreamIndex);

public static class SourceProbe
{
	public static SourceInfo Probe(string inputPath)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffprobe",
			"-v", "error", "-print_format", "json", "-show_streams", "-show_format", inputPath);

		using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffprobe.");
		var stdout = process.StandardOutput.ReadToEnd();
		var stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();

		if (process.ExitCode != 0)
			throw new InvalidOperationException($"ffprobe exited with an error ({process.ExitCode}): {stderr}");

		JsonNode root = JsonNode.Parse(stdout) ?? throw new InvalidOperationException("Empty ffprobe output.");
		JsonArray streams = root["streams"]!.AsArray();

		JsonNode? videoStream = null;
		JsonNode? audioStream = null;
		var hasDjmd = false;
		int? djmdStreamIndex = null;

		foreach (JsonNode? s in streams)
		{
			var codecType = s!["codec_type"]?.GetValue<string>();
			var codecTag = s["codec_tag_string"]?.GetValue<string>();

			switch (codecType)
			{
				case "video" when videoStream is null:
					videoStream = s;
					break;
				case "audio" when audioStream is null:
					audioStream = s;
					break;
				case "data" when codecTag == "djmd":
					hasDjmd = true;
					djmdStreamIndex = s["index"]?.GetValue<int>();
					break;
			}
		}

		if (videoStream is null)
			throw new InvalidOperationException($"No video stream found in {inputPath}.");

		var video = new VideoInfo(
			videoStream["codec_name"]!.GetValue<string>(),
			videoStream["profile"]?.GetValue<string>() ?? "",
			videoStream["width"]!.GetValue<int>(),
			videoStream["height"]!.GetValue<int>(),
			videoStream["r_frame_rate"]!.GetValue<string>(),
			videoStream["pix_fmt"]?.GetValue<string>() ?? "yuv420p",
			videoStream["color_primaries"]?.GetValue<string>(),
			videoStream["color_transfer"]?.GetValue<string>(),
			videoStream["color_space"]?.GetValue<string>(),
			videoStream["color_range"]?.GetValue<string>(),
			ResolveVideoBitRate(videoStream, audioStream, root));

		AudioInfo? audio = audioStream is null
			? null
			: new AudioInfo(
				audioStream["codec_name"]!.GetValue<string>(),
				int.Parse(audioStream["sample_rate"]!.GetValue<string>()),
				audioStream["channels"]!.GetValue<int>());

		var duration = double.Parse(root["format"]!["duration"]!.GetValue<string>(), CultureInfo.InvariantCulture);

		return new SourceInfo(video, audio, hasDjmd, duration, djmdStreamIndex);
	}

	private static long ResolveVideoBitRate(JsonNode videoStream, JsonNode? audioStream, JsonNode root)
	{
		var videoBitRate = videoStream["bit_rate"]?.GetValue<string>();
		if (videoBitRate is not null) return long.Parse(videoBitRate);

		var formatBitRateStr = root["format"]?["bit_rate"]?.GetValue<string>()
		                       ?? throw new InvalidOperationException(
			                       "ffprobe reported no bit_rate for the video stream or the container format.");
		var formatBitRate = long.Parse(formatBitRateStr);

		// format.bit_rate covers every stream in the container; when the video stream doesn't
		// report its own rate, subtract the audio track's so we don't inflate the video target.
		var audioBitRate = audioStream?["bit_rate"]?.GetValue<string>();
		return audioBitRate is not null ? Math.Max(0, formatBitRate - long.Parse(audioBitRate)) : formatBitRate;
	}
}
