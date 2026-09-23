using System.Diagnostics;

namespace OsmoOverlay.Gui;

internal static class ExplorerHelper
{
	/// <summary>
	///     Opens the platform's file manager with the given file pre-selected (Explorer, Finder), e.g. as
	///     proof a render/fix actually produced an output file - or just its folder on Linux, where there's
	///     no file-manager-agnostic way to select a file. UseShellExecute so this runs like a user
	///     double-clicking it, not like the CreateHidden/redirected-output pattern used for ffmpeg/ffprobe/
	///     exiftool elsewhere.
	/// </summary>
	public static void ShowInFolder(string filePath)
	{
		try
		{
			ProcessStartInfo psi;
			if (OperatingSystem.IsWindows())
			{
				psi = new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{filePath}\"", UseShellExecute = true };
			}
			else if (OperatingSystem.IsMacOS())
			{
				psi = new ProcessStartInfo("open") { ArgumentList = { "-R", filePath } };
			}
			else
			{
				psi = new ProcessStartInfo(Path.GetDirectoryName(filePath) ?? filePath) { UseShellExecute = true };
			}

			Process.Start(psi)?.Dispose();
		}
		catch
		{
			// Best-effort - whatever dialog led here already confirmed success, so a failure to open
			// Explorer isn't worth surfacing as its own error.
		}
	}
}
