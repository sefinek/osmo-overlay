using Avalonia;
using OsmoOverlay.Cameras.Dji;
using OsmoOverlay.Cameras.GoPro;
using OsmoOverlay.Cameras.Insta360;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry.External;
using OsmoOverlay.Core.Updates;
using OsmoOverlay.Dji.Drones;
using OsmoOverlay.Gui.Native;

namespace OsmoOverlay.Gui;

internal class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		if (SmokeTest.IsRequested(args)) SmokeTest.Start();

		// Held while the app runs - the installer's AppMutex, so an update waits for the app to close.
		using var appMutex = new Mutex(false, AppUpdates.MutexName);
		OverlaySettings settings = OverlaySettingsStore.Load();
		StartupKeys.Capture();
		AppLogger.Verbose = settings.DebugMode || StartupKeys.ShiftHeld;
		UiLanguages.Apply(settings.UiLanguage);
		CameraFormats.Register(new DjiOsmoFormat(), new GoProFormat(), new Insta360Format(), new DjiDroneFormat());
		ExternalTrackReader.Register(new DjiFlightLogFormat());
		UiScale.Initialize();
		return BuildAvaloniaApp()
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
