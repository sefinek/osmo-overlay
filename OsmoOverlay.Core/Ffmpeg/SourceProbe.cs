using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
	long BitRate,
	// Encoder-structure details of the source a render reproduces (see FfmpegPipeline.StartRender) -
	// Level in ffmpeg's own units (level * 30, e.g. 156 = 5.2), 0 when unknown; HighTier/
	// KeyframeIntervalFrames null when they couldn't be read; Timecode as the camera wrote it
	// (e.g. "07:33:40;28").
	int Level = 0,
	int? KeyframeIntervalFrames = null,
	string? Timecode = null,
	bool? HighTier = null,
	// The container's own frame count for this stream (nb_frames) - exact, unlike duration * fps, which
	// the container duration (running past the last video frame to where the audio ends) overshoots.
	long? FrameCount = null)
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

public static partial class SourceProbe
{
	public static SourceInfo Probe(string inputPath)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffprobe",
			"-v", "error", "-print_format", "json", "-show_streams", "-show_format", inputPath);

		var (exitCode, stdout, stderr) = ProcessHelper.RunCaptured(psi);
		if (exitCode != 0)
			throw new InvalidOperationException($"ffprobe exited with an error ({exitCode}): {stderr}");

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
			ResolveVideoBitRate(videoStream, audioStream, root),
			videoStream["level"]?.GetValue<int>() ?? 0,
			ProbeKeyframeInterval(inputPath, videoStream["r_frame_rate"]!.GetValue<string>()),
			ResolveTimecode(videoStream, streams, root),
			videoStream["codec_name"]?.GetValue<string>() == "hevc" ? ProbeHevcHighTier(inputPath) : null,
			long.TryParse(videoStream["nb_frames"]?.GetValue<string>(), CultureInfo.InvariantCulture, out var frameCount) && frameCount > 0
				? frameCount
				: null);

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

	/// <summary>
	///     Frames between consecutive keyframes over the first few seconds (the camera uses a fixed GOP -
	///     every 60 frames at 59.94p on an Osmo Action 6), as the median gap so one odd keyframe can't skew
	///     it. Null if it can't be measured; best-effort, a failure here must never fail the probe itself.
	/// </summary>
	private static int? ProbeKeyframeInterval(string inputPath, string frameRate)
	{
		try
		{
			var parts = frameRate.Split('/');
			var fps = parts.Length > 1
				? double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture)
				: double.Parse(parts[0], CultureInfo.InvariantCulture);
			if (!(fps > 0)) return null;

			ProcessStartInfo psi = ProcessHelper.CreateHiddenQuiet("ffprobe",
				"-v", "error", "-select_streams", "v:0", "-skip_frame", "nokey", "-read_intervals", "%+6",
				"-show_entries", "frame=pts_time", "-of", "csv=p=0", inputPath);
			var (exitCode, stdout, _) = ProcessHelper.RunCaptured(psi);
			if (exitCode != 0) return null;

			List<double> times = [.. stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(line => double.TryParse(line, CultureInfo.InvariantCulture, out var t) ? t : double.NaN)
				.Where(double.IsFinite)];
			if (times.Count < 2) return null;

			List<int> gaps = [.. times.Zip(times.Skip(1), (a, b) => (int)Math.Round((b - a) * fps)).Where(g => g > 0).Order()];
			return gaps.Count > 0 ? gaps[gaps.Count / 2] : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	///     The HEVC general_tier_flag from the stream's own parameter sets - ffprobe doesn't report it, and it
	///     matters: the camera's 73-90 Mbps at level 5.2 is only legal in the high tier (main tier tops out at
	///     60 Mbps there), so an encoder asked for that level at that rate refuses to start without it.
	///     Reads the headers of a single packet; null on any failure.
	/// </summary>
	private static bool? ProbeHevcHighTier(string inputPath)
	{
		try
		{
			ProcessStartInfo psi = ProcessHelper.CreateHiddenQuiet("ffmpeg",
				"-hide_banner", "-loglevel", "trace", "-i", inputPath, "-map", "0:v:0", "-frames:v", "1", "-c", "copy",
				"-bsf:v", "trace_headers", "-f", "null", "-");
			var (_, _, stderr) = ProcessHelper.RunCaptured(psi);
			Match match = TierFlagRegex().Match(stderr);
			return match.Success ? match.Groups[1].Value == "1" : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	[GeneratedRegex(@"general_tier_flag\s+\d+\s*=\s*(\d)")]
	private static partial Regex TierFlagRegex();

	private static string? ResolveTimecode(JsonNode videoStream, JsonArray streams, JsonNode root)
	{
		return videoStream["tags"]?["timecode"]?.GetValue<string>()
		       ?? streams.Select(s => s?["tags"]?["timecode"]?.GetValue<string>()).FirstOrDefault(t => t is not null)
		       ?? root["format"]?["tags"]?["timecode"]?.GetValue<string>();
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
