using System.Diagnostics;
using System.Text.Json.Nodes;

namespace OsmoOverlay.Core.Ffmpeg;

public sealed record MetadataStripResult(string OutputPath, IReadOnlyList<string> Removed);

/// <summary>
///     Produces a copy safe to share: only the picture and sound streams, bit-for-bit (stream copy, no
///     re-encode), with nothing that identifies the camera, the place or the time. Verified against an
///     Osmo Action 6 original, which carries:
///     - djmd track: GPS track, accelerometer, and the camera's serial number
///     - dbgi track: camera debug data
///     - tmcd track + timecode tags: time of day the recording started
///     - an MJPEG thumbnail (attached picture)
///     - creation_time on the container and every track, the camera's encoder string, DJI udta boxes
///     The HEVC bitstream itself has no SEI messages, so nothing is hidden inside the picture data.
///     Everything describing how to display the picture stays: color primaries/transfer/matrix/range (in
///     the codec parameters, written back as the colr box), the rotation (display matrix), profile,
///     level and the hvc1 tag. mvhd/tkhd/mdhd times come out as 0.
///     The output is probed afterwards (Verify) and never kept unless it passes.
/// </summary>
public static class MetadataStripper
{
	private static readonly HashSet<string> AllowedFormatTags = ["major_brand", "minor_version", "compatible_brands"];
	private static readonly HashSet<string> AllowedStreamTags = ["language", "handler_name", "vendor_id"];

	public static MetadataStripResult Strip(string inputPath, string outputPath)
	{
		if (Path.GetFullPath(inputPath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("The output must be a different file than the input.");

		JsonNode source = ProbeJson(inputPath);

		// Written next to the target and only moved into place after Verify passes, so a failed or
		// unverifiable run can never leave a half-clean file under the name the user picked.
		var partialPath = outputPath + ".partial";
		try
		{
			ProcessStartInfo psi = ProcessHelper.CreateHidden("ffmpeg",
				"-hide_banner", "-loglevel", "error", "-y",
				"-i", inputPath,
				// 0:V (capital) is video without attached pictures, so the embedded thumbnail is dropped.
				"-map", "0:V", "-map", "0:a?",
				"-c", "copy",
				"-map_metadata", "-1", "-map_metadata:s", "-1", "-map_chapters", "-1",
				// No "Lavf..." encoder tag, no muxer-generated identifiers.
				"-fflags", "+bitexact",
				"-f", "mp4",
				partialPath);

			var (exitCode, stdout, stderr) = ProcessHelper.RunCaptured(psi);
			if (exitCode != 0)
				throw new InvalidOperationException($"ffmpeg exited with an error ({exitCode}): {stderr}{stdout}");

			Verify(source, ProbeJson(partialPath));
			File.Move(partialPath, outputPath, true);
		}
		finally
		{
			TryDelete(partialPath);
		}

		return new MetadataStripResult(outputPath, DescribeRemoved(source));
	}

	/// <summary>Throws unless the output has nothing but the source's own video/audio, untouched, and no identifying tags.</summary>
	internal static void Verify(JsonNode source, JsonNode output)
	{
		// Same order ffmpeg writes them in: -map 0:V first, then -map 0:a.
		List<JsonNode> sourceKept =
		[
			.. Streams(source).Where(s => IsKeptStream(s) && Str(s, "codec_type") == "video"),
			.. Streams(source).Where(s => Str(s, "codec_type") == "audio")
		];
		List<JsonNode> outputStreams = [.. Streams(output)];

		if (outputStreams.Any(s => !IsKeptStream(s)))
			throw new InvalidOperationException("Verification failed: the output still contains a data, timecode or thumbnail stream.");
		if (outputStreams.Count != sourceKept.Count)
			throw new InvalidOperationException(
				$"Verification failed: expected {sourceKept.Count} video/audio stream(s), got {outputStreams.Count}.");

		if (Tags(output["format"]).Keys.FirstOrDefault(k => !AllowedFormatTags.Contains(k)) is { } formatTag)
			throw new InvalidOperationException($"Verification failed: the container still has a '{formatTag}' tag.");

		for (var i = 0; i < outputStreams.Count; i++)
		{
			JsonNode before = sourceKept[i];
			JsonNode after = outputStreams[i];

			if (Tags(after).Keys.FirstOrDefault(k => !AllowedStreamTags.Contains(k)) is { } streamTag)
				throw new InvalidOperationException($"Verification failed: stream #{i} still has a '{streamTag}' tag.");

			// Same packets and same display parameters - a stream copy must not change the picture or its color.
			string[] mustMatch =
			[
				"codec_type", "codec_name", "profile", "level", "width", "height", "pix_fmt",
				"color_primaries", "color_transfer", "color_space", "color_range", "sample_rate", "channels", "nb_frames"
			];
			foreach (var key in mustMatch)
				if (Str(before, key) != Str(after, key))
					throw new InvalidOperationException(
						$"Verification failed: stream #{i} {key} changed ({Str(before, key) ?? "none"} -> {Str(after, key) ?? "none"}).");

			if (Rotation(before) != Rotation(after))
				throw new InvalidOperationException("Verification failed: the video rotation changed.");
		}
	}

	private static List<string> DescribeRemoved(JsonNode source)
	{
		List<string> removed = [];
		foreach (JsonNode stream in Streams(source).Where(s => !IsKeptStream(s)))
			removed.Add(Str(stream, "codec_tag_string") switch
			{
				"djmd" => "DJI telemetry track (GPS track, sensors, camera serial number)",
				"dbgi" => "DJI debug track",
				"tmcd" => "timecode track (time of day)",
				_ when IsAttachedPicture(stream) => "embedded thumbnail",
				_ => $"{Str(stream, "codec_type")} stream ({Str(stream, "codec_tag_string") ?? Str(stream, "codec_name")})"
			});

		var tagCount = Tags(source["format"]).Keys.Count(k => !AllowedFormatTags.Contains(k)) +
		               Streams(source).Sum(s => Tags(s).Keys.Count(k => !AllowedStreamTags.Contains(k)));
		if (tagCount > 0) removed.Add($"{tagCount} metadata tag(s) (recording date, timecode, encoder...)");
		return removed;
	}

	private static bool IsKeptStream(JsonNode stream)
	{
		return Str(stream, "codec_type") switch
		{
			"video" => !IsAttachedPicture(stream),
			"audio" => true,
			_ => false
		};
	}

	private static bool IsAttachedPicture(JsonNode stream)
	{
		return stream["disposition"]?["attached_pic"]?.GetValue<int>() == 1;
	}

	private static string? Rotation(JsonNode stream)
	{
		return (stream["side_data_list"] as JsonArray)?
			.FirstOrDefault(d => Str(d, "side_data_type") == "Display Matrix") is { } matrix
			? Str(matrix, "rotation")
			: null;
	}

	private static IEnumerable<JsonNode> Streams(JsonNode root)
	{
		return (root["streams"] as JsonArray)?.OfType<JsonNode>() ?? [];
	}

	private static Dictionary<string, string?> Tags(JsonNode? node)
	{
		return (node?["tags"] as JsonObject)?.ToDictionary(p => p.Key, p => p.Value?.ToString()) ?? [];
	}

	private static string? Str(JsonNode? node, string key)
	{
		return node?[key]?.ToString();
	}

	private static JsonNode ProbeJson(string path)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHidden("ffprobe",
			"-v", "error", "-print_format", "json", "-show_streams", "-show_format", path);

		var (exitCode, stdout, stderr) = ProcessHelper.RunCaptured(psi);
		if (exitCode != 0) throw new InvalidOperationException($"ffprobe exited with an error ({exitCode}): {stderr}");
		return JsonNode.Parse(stdout) ?? throw new InvalidOperationException("Empty ffprobe output.");
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Best-effort: a leftover .partial file is harmless.
		}
	}
}
