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
/// </summary>
internal static class ConcatListWriter
{
	public static string Write(IEnumerable<(string Path, double? InpointSeconds)> entries)
	{
		var listPath = Path.Combine(Path.GetTempPath(), $"osmooverlay_concat_{Guid.NewGuid():N}.txt");

		var lines = new List<string>();
		foreach (var (path, inpointSeconds) in entries)
		{
			lines.Add($"file '{Escape(path)}'");
			if (inpointSeconds is > 0)
				lines.Add($"inpoint {inpointSeconds.Value.ToString(CultureInfo.InvariantCulture)}");
		}

		File.WriteAllLines(listPath, lines);
		return listPath;
	}

	private static string Escape(string path)
	{
		// Forward slashes work fine on Windows, and a literal ' inside a 'file ...' entry must be
		// escaped as '\'' per the concat demuxer's quoting rules.
		return path.Replace("\\", "/").Replace("'", "'\\''");
	}
}
