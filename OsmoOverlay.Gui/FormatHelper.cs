namespace OsmoOverlay.Gui;

internal static class FormatHelper
{
	public static string FormatBytes(long bytes)
	{
		var gb = bytes / 1_073_741_824.0;
		return gb >= 1 ? $"{gb:0.##} GB" : $"{bytes / 1_048_576.0:0.#} MB";
	}
}
