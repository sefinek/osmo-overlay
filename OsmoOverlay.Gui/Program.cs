using Avalonia;
using OsmoOverlay.Core.Updates;

namespace OsmoOverlay.Gui;

internal class Program
{
	[STAThread]
	public static void Main(string[] args)
	{
		// Held while the app runs - the installer's AppMutex, so an update waits for the app to close.
		using var appMutex = new Mutex(false, AppUpdates.MutexName);
		BuildAvaloniaApp()
			.StartWithClassicDesktopLifetime(args);
	}

	public static AppBuilder BuildAvaloniaApp()
	{
		return AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.WithInterFont()
			.LogToTrace();
	}
}
