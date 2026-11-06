using Avalonia;
using Telemavix.Cameras.Dji;
using Telemavix.Cameras.GoPro;
using Telemavix.Cameras.Insta360;
using Telemavix.Core;
using Telemavix.Core.Cameras;
using Telemavix.Core.Localization;
using Telemavix.Core.Mapping;
using Telemavix.Core.Logging;
using Telemavix.Core.Overlay;
using Telemavix.Core.Telemetry.External;
using Telemavix.Core.Updates;
using Telemavix.Drones.Dji;
using Telemavix.Gui.Native;

namespace Telemavix.Gui;

internal class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		LegacyDataFolder.Migrate();
		if (SmokeTest.IsRequested(args)) SmokeTest.Start();

		// Held while the app runs - the installer's AppMutex, so an update waits for the app to close.
		using var appMutex = new Mutex(false, AppUpdates.MutexName);
		OverlaySettings settings = OverlaySettingsStore.Load();
		_ = MapKeyCheck.EnsureAsync(settings.MapApiKeys);
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
