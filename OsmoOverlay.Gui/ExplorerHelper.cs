using System.Diagnostics;

namespace OsmoOverlay.Gui;

internal static class ExplorerHelper
{
	/// <summary>
	///     Opens Explorer with the given file pre-selected, e.g. as proof a render/fix actually produced
	///     an output file - UseShellExecute so this runs like a user double-clicking it, not like the
	///     CreateHidden/redirected-output pattern used for ffmpeg/ffprobe/exiftool elsewhere.
	/// </summary>
	public static void ShowInFolder(string filePath)
	{
		try
		{
			Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{filePath}\"", UseShellExecute = true });
		}
		catch
		{
			// Best-effort - whatever dialog led here already confirmed success, so a failure to open
			// Explorer isn't worth surfacing as its own error.
		}
	}
}
