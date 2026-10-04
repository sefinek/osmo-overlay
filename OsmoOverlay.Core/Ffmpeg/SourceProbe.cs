using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Reframe;

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
	public double Fps => ParseFps(FrameRate);

	/// <summary>An ffprobe rate ("60000/1001" or "30") as frames a second.</summary>
	internal static double ParseFps(string frameRate)
	{
		string[] parts = frameRate.Split('/');
		return parts.Length > 1
			? double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture)
			: double.Parse(parts[0], CultureInfo.InvariantCulture);
	}
}

/// <summary>StreamId is the container's own track id as ffprobe reports it (e.g. "0x2") - what a concat list's exact_stream_id needs.</summary>
public sealed record AudioInfo(string CodecName, int SampleRate, int Channels, long BitRate, string? StreamId = null);

public sealed record SourceInfo(
	VideoInfo Video,
	AudioInfo? Audio,
	double DurationSeconds,
	// The container's own "when did recording start" (its creation_time tag, written by the camera
	// itself) - independent of GPS, so it's the only usable fallback for Date&Time/UTC time on a
	// recording with no GPS timestamp at all (e.g. filmed indoors, no fix ever acquired). Null when
	// the container has no such tag or it fails to parse - callers must treat that as "no fallback
	// available", not "recording started at DateTime default".
	DateTime? ContainerCreationTimeUtc,
	// The camera that recorded the file (CameraFormats.Detect) - null for one no registered camera knows, i.e. no telemetry.
	CameraRecording? Camera = null)
{
	/// <summary>
	///     A 360 recording's lenses - Video then describes the flat picture reframed from them (Reframing.OutputSize), the
	///     picture everything else in the app works with; only the render's and the preview's decoding see the lenses.
	/// </summary>
	public DualFisheye? Fisheye => Camera?.Lenses;
}

public static partial class SourceProbe
{
	/// <param name="encoderDetails">
	///     Whether to measure the GOP and read the HEVC tier (two more processes, ~2 s) - only a render's first file is
	///     encoded like (OutputVideo.For); the files after it must match it anyway (VideoSegments.FindMismatch).
	/// </param>
	public static SourceInfo Probe(string inputPath, bool encoderDetails = true)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffprobe",
			"-v", "error", "-print_format", "json", "-show_streams", "-show_format", inputPath);

		(int exitCode, string stdout, string stderr) = ProcessHelper.RunCaptured(psi);
		if (exitCode != 0)
			throw new InvalidOperationException($"ffprobe exited with an error ({exitCode}): {stderr}");

		JsonNode root = JsonNode.Parse(stdout) ?? throw new InvalidOperationException("Empty ffprobe output.");
		JsonArray streams = root["streams"]!.AsArray();

		JsonNode? videoStream = null;
		JsonNode? secondVideoStream = null;
		JsonNode? audioStream = null;

		foreach (JsonNode? s in streams)
		{
			string? codecType = s!["codec_type"]?.GetValue<string>();

			switch (codecType)
			{
				case "video" when videoStream is null:
					videoStream = s;
					break;
				case "video" when secondVideoStream is null && !IsAttachedPicture(s):
					secondVideoStream = s;
					break;
				case "audio" when audioStream is null:
					audioStream = s;
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
			encoderDetails ? ProbeKeyframeInterval(inputPath, videoStream["r_frame_rate"]!.GetValue<string>()) : null,
			ResolveTimecode(videoStream, streams, root),
			encoderDetails && videoStream["codec_name"]?.GetValue<string>() == "hevc" ? ProbeHevcHighTier(inputPath) : null,
			long.TryParse(videoStream["nb_frames"]?.GetValue<string>(), CultureInfo.InvariantCulture, out long frameCount) && frameCount > 0
				? frameCount
				: null);

		AudioInfo? audio = audioStream is null
			? null
			: new AudioInfo(
				audioStream["codec_name"]!.GetValue<string>(),
				int.Parse(audioStream["sample_rate"]!.GetValue<string>()),
				audioStream["channels"]!.GetValue<int>(),
				ResolveAudioBitRate(audioStream, videoStream, root),
				audioStream["id"]?.GetValue<string>());

		double duration = double.Parse(root["format"]!["duration"]!.GetValue<string>(), CultureInfo.InvariantCulture);

		DateTime? containerCreationTimeUtc = ParseCreationTime(root["format"]?["tags"]?["creation_time"]?.GetValue<string>());
		CameraRecording? camera = CameraFormats.Detect(inputPath, streams);
		if (camera?.Lenses is { } lenses) video = Reframed(video, lenses, secondVideoStream);
		return new SourceInfo(video, audio, duration, containerCreationTimeUtc, camera);
	}

	/// <summary>The flat picture reframed from the lenses - its size, and the lenses' bitrate per pixel kept at that size.</summary>
	private static VideoInfo Reframed(VideoInfo lenses, DualFisheye fisheye, JsonNode? secondStream)
	{
		(int width, int height) = Reframing.OutputSize(fisheye);
		long lensBitRate = lenses.BitRate +
		                   (long.TryParse(secondStream?["bit_rate"]?.GetValue<string>(), CultureInfo.InvariantCulture, out long second) ? second : 0);
		double lensPixels = fisheye.Layout == FisheyeLayout.TwoStreams ? 2.0 * lenses.Width * lenses.Height : (double)lenses.Width * lenses.Height;
		return lenses with { Width = width, Height = height, BitRate = (long)Math.Round(lensBitRate * (width * (double)height / lensPixels)) };
	}

	private static bool IsAttachedPicture(JsonNode stream)
	{
		return stream["disposition"]?["attached_pic"]?.GetValue<int>() == 1;
	}

	private static DateTime? ParseCreationTime(string? text)
	{
		return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
			out DateTime parsed)
			? parsed
			: null;
	}

	/// <summary>
	///     The start time ffmpeg will see for a concat list input, as the exact string ffprobe prints - the
	///     -itsoffset ConcatListWriter.WriteAudioOnly's list needs.
	/// </summary>
	public static string ProbeConcatStartTime(string listPath)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffprobe",
			"-v", "error", "-f", "concat", "-safe", "0", "-show_entries", "format=start_time", "-of", "csv=p=0", listPath);

		(int exitCode, string stdout, string stderr) = ProcessHelper.RunCaptured(psi);
		string startTime = stdout.Trim();
		if (exitCode != 0 || !double.TryParse(startTime, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
			throw new InvalidOperationException($"Couldn't read the start time of the audio for this range: {stderr}");
		return startTime;
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
			double fps = VideoInfo.ParseFps(frameRate);
			if (!(fps > 0)) return null;

			ProcessStartInfo psi = ProcessHelper.CreateHiddenQuiet("ffprobe",
				"-v", "error", "-select_streams", "v:0", "-skip_frame", "nokey", "-read_intervals", "%+6",
				"-show_entries", "frame=pts_time", "-of", "csv=p=0", inputPath);
			(int exitCode, string stdout, _) = ProcessHelper.RunCaptured(psi);
			if (exitCode != 0) return null;

			List<double> times =
			[
				.. stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Select(line => double.TryParse(line, CultureInfo.InvariantCulture, out double t) ? t : double.NaN)
					.Where(double.IsFinite)
			];
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
			(_, _, string stderr) = ProcessHelper.RunCaptured(psi);
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
		string? videoBitRate = videoStream["bit_rate"]?.GetValue<string>();
		if (videoBitRate is not null) return long.Parse(videoBitRate);

		string formatBitRateStr = root["format"]?["bit_rate"]?.GetValue<string>()
		                          ?? throw new InvalidOperationException(
			                          "ffprobe reported no bit_rate for the video stream or the container format.");
		long formatBitRate = long.Parse(formatBitRateStr);

		// format.bit_rate covers every stream in the container; when the video stream doesn't
		// report its own rate, subtract the audio track's so we don't inflate the video target.
		string? audioBitRate = audioStream?["bit_rate"]?.GetValue<string>();
		return audioBitRate is not null ? Math.Max(0, formatBitRate - long.Parse(audioBitRate)) : formatBitRate;
	}

	private static long ResolveAudioBitRate(JsonNode audioStream, JsonNode videoStream, JsonNode root)
	{
		string? audioBitRate = audioStream["bit_rate"]?.GetValue<string>();
		if (audioBitRate is not null) return long.Parse(audioBitRate);

		// Mirrors ResolveVideoBitRate: when the audio stream doesn't report its own rate, derive it
		// from the container total minus the video track's rate instead of showing 0.
		string? formatBitRateStr = root["format"]?["bit_rate"]?.GetValue<string>();
		if (formatBitRateStr is null) return 0;

		string? videoBitRate = videoStream["bit_rate"]?.GetValue<string>();
		if (videoBitRate is null) return 0;

		return Math.Max(0, long.Parse(formatBitRateStr) - long.Parse(videoBitRate));
	}
}
