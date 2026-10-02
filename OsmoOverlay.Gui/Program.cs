using Avalonia;
using OsmoOverlay.Cameras.Dji;
using OsmoOverlay.Cameras.Insta360;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Updates;

namespace OsmoOverlay.Gui;

internal class Program
{
	[STAThread]
	public static void Main(string[] args)
	{
		// Held while the app runs - the installer's AppMutex, so an update waits for the app to close.
		using var appMutex = new Mutex(false, AppUpdates.MutexName);
		UiLanguages.Apply(OverlaySettingsStore.Load().UiLanguage);
		CameraFormats.Register(new DjiOsmoFormat(), new Insta360Format());
		UiScale.Initialize();
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
