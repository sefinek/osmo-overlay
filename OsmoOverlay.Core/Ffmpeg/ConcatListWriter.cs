using System.Globalization;

namespace OsmoOverlay.Core.Ffmpeg;

/// <summary>
///     Writes a temporary list file in ffmpeg's concat demuxer format (
///     <c>
///         -f concat -safe 0 -i
///         &lt;file&gt;
///     </c>
///     ), shared by the full render pipeline (FfmpegPipeline) and the live preview
///     (VideoFrameSource) so both stitch multi-segment recordings the same way.
///     No "inpoint" for video - confirmed against a real multi-segment recording that the concat
///     demuxer's own seek (inpoint, or a top-level -ss before -i) decodes a stuck, repeated frame (or
///     breaks reference frames entirely) instead of actually seeking, regardless of hwaccel. Video
///     entries always start at their own beginning; a seek into the middle of a segment goes through a
///     plain single-file -ss instead (VideoFrameSource.OpenPlaybackStream, FfmpegPipeline.StartRender).
///     Audio has no reference frames, so WriteAudioOnly can use inpoint (see there).
/// </summary>
internal static class ConcatListWriter
{
	private const string FilePrefix = "osmooverlay_concat_";

	public static string Write(IEnumerable<string> paths)
	{
		var listPath = NewListPath();
		File.WriteAllLines(listPath, paths.Select(p => $"file '{Escape(p)}'"));
		return listPath;
	}

	/// <summary>
	///     Audio-only list starting `firstInpointSeconds` into the first file - how a render range that starts
	///     mid-segment and continues into the next ones gets its audio as a stream copy. Two things verified
	///     on real Osmo recordings that this depends on:
	///     - the `stream`/exact_stream_id directive: without it the list also carries the camera's thumbnail
	///     stream, whose start time (shifted by -inpoint) drags the whole input's start time back, so an
	///     output -t cut every audio packet
	///     - the demuxer still emits the packets from the preceding video keyframe on (~0.35 s before
	///     inpoint, at negative timestamps); the caller must pass the list's probed start time as
	///     -itsoffset so those stay negative and the MP4 edit list hides them, otherwise audio leads
	///     the picture by exactly that much
	/// </summary>
	public static string WriteAudioOnly(IReadOnlyList<string> paths, string audioStreamId, double firstInpointSeconds)
	{
		var listPath = NewListPath();
		List<string> lines = ["ffconcat version 1.0", "stream", $"exact_stream_id {audioStreamId}"];
		for (var i = 0; i < paths.Count; i++)
		{
			lines.Add($"file '{Escape(paths[i])}'");
			if (i == 0) lines.Add($"inpoint {firstInpointSeconds.ToString("R", CultureInfo.InvariantCulture)}");
		}

		File.WriteAllLines(listPath, lines);
		return listPath;
	}

	/// <summary>
	///     Lists are deleted once their ffmpeg exits (FfmpegPipeline, VideoPlaybackStream), but a crash or a
	///     killed app skips that. Removes whatever earlier runs left behind - only lists older than
	///     `olderThan`, so one a running render or preview still uses is never touched.
	/// </summary>
	public static int DeleteStale(TimeSpan olderThan)
	{
		var deleted = 0;
		DateTime cutoff = DateTime.UtcNow - olderThan;
		foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), FilePrefix + "*.txt"))
			try
			{
				if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
				File.Delete(path);
				deleted++;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Best-effort: another instance may be using it, or it's already gone.
			}

		return deleted;
	}

	private static string NewListPath()
	{
		return Path.Combine(Path.GetTempPath(), $"{FilePrefix}{Guid.NewGuid():N}.txt");
	}

	private static string Escape(string path)
	{
		// Forward slashes work fine on Windows, and a literal ' inside a 'file ...' entry must be
		// escaped as '\'' per the concat demuxer's quoting rules.
		return path.Replace("\\", "/").Replace("'", "'\\''");
	}
}
