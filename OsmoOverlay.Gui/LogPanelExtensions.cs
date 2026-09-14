using Avalonia.Controls;

namespace OsmoOverlay.Gui;

internal static class LogPanelExtensions
{
	public static void AppendLog(this TextBox logBox, ScrollViewer logScroll, string message)
	{
		logBox.Text += message + "\n";
		logScroll.ScrollToEnd();
	}
}
