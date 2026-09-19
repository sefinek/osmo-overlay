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

public sealed record AudioInfo(string CodecName, int SampleRate, int Channels, long BitRate);

public sealed record SourceInfo(
	VideoInfo Video,
	AudioInfo? Audio,
	bool HasDjmdTrack,
	double DurationSeconds,
	int? DjmdStreamIndex,
	// The container's own "when did recording start" (its creation_time tag, written by the camera
	// itself) - independent of GPS, so it's the only usable fallback for Date&Time/UTC time on a
	// recording with no GPS timestamp at all (e.g. filmed indoors, no fix ever acquired). Null when
	// the container has no such tag or it fails to parse - callers must treat that as "no fallback
	// available", not "recording started at DateTime default".
	DateTime? ContainerCreationTimeUtc);

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
				audioStream["channels"]!.GetValue<int>(),
				ResolveAudioBitRate(audioStream, videoStream, root));

		var duration = double.Parse(root["format"]!["duration"]!.GetValue<string>(), CultureInfo.InvariantCulture);

		DateTime? containerCreationTimeUtc = null;
		if (root["format"]?["tags"]?["creation_time"]?.GetValue<string>() is { } creationTimeStr &&
		    DateTime.TryParse(creationTimeStr, CultureInfo.InvariantCulture,
			    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
			containerCreationTimeUtc = parsed;

		return new SourceInfo(video, audio, hasDjmd, duration, djmdStreamIndex, containerCreationTimeUtc);
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

	private static long ResolveAudioBitRate(JsonNode audioStream, JsonNode videoStream, JsonNode root)
	{
		var audioBitRate = audioStream["bit_rate"]?.GetValue<string>();
		if (audioBitRate is not null) return long.Parse(audioBitRate);

		// Mirrors ResolveVideoBitRate: when the audio stream doesn't report its own rate, derive it
		// from the container total minus the video track's rate instead of showing 0.
		var formatBitRateStr = root["format"]?["bit_rate"]?.GetValue<string>();
		if (formatBitRateStr is null) return 0;

		var videoBitRate = videoStream["bit_rate"]?.GetValue<string>();
		if (videoBitRate is null) return 0;

		return Math.Max(0, long.Parse(formatBitRateStr) - long.Parse(videoBitRate));
	}
}
