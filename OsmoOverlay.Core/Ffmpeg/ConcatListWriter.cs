namespace OsmoOverlay.Core.Ffmpeg;

/// <summary>
///     Writes a temporary list file in ffmpeg's concat demuxer format (
///     <c>
///         -f concat -safe 0 -i
///         &lt;file&gt;
///     </c>
///     ), shared by the full render pipeline (FfmpegPipeline) and the live preview
///     (VideoFrameSource) so both stitch multi-segment recordings the same way.
///     No per-entry "inpoint" support - confirmed against a real multi-segment recording that the
///     concat demuxer's own seek (inpoint, or a top-level -ss before -i) decodes a stuck, repeated
///     frame (or breaks reference frames entirely) instead of actually seeking, regardless of
///     hwaccel. Entries here always start at their own beginning; VideoFrameSource handles seeking
///     into the middle of a segment itself, via a plain single-file -ss (see OpenPlaybackStream).
/// </summary>
internal static class ConcatListWriter
{
	public static string Write(IEnumerable<string> paths)
	{
		var listPath = Path.Combine(Path.GetTempPath(), $"osmooverlay_concat_{Guid.NewGuid():N}.txt");
		File.WriteAllLines(listPath, paths.Select(p => $"file '{Escape(p)}'"));
		return listPath;
	}

	private static string Escape(string path)
	{
		// Forward slashes work fine on Windows, and a literal ' inside a 'file ...' entry must be
		// escaped as '\'' per the concat demuxer's quoting rules.
		return path.Replace("\\", "/").Replace("'", "'\\''");
	}
}
