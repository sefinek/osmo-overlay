using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

public class App : Application
{
	public override void Initialize()
	{
		AvaloniaXamlLoader.Load(this);
		AppThemes.Apply(OverlaySettingsStore.Load().AppTheme);
		UiScale.Configure(this);
		FocusRelease.Configure();
	}

	public override void OnFrameworkInitializationCompleted()
	{
		// Every button handler is async void - without this, one unexpected exception in any of them
		// takes the whole app down with nothing in app.log. Logged through AppLogger.Error, which the
		// main window mirrors into its LOG panel, and marked handled so the window stays usable.
		Dispatcher.UIThread.UnhandledException += (_, e) =>
		{
			AppLogger.Error(e.Exception, string.Format(Strings.App_UnexpectedError, e.Exception.Message));
			e.Handled = true;
		};
		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			AppLogger.Error(e.Exception, string.Format(Strings.App_BackgroundError, e.Exception.GetBaseException().Message));
			e.SetObserved();
		};
		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
		{
			if (e.ExceptionObject is Exception ex) AppLogger.Error(ex, string.Format(Strings.App_FatalError, ex.Message));
		};

		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
			desktop.MainWindow = new MainWindow { StartupProject = StartupProjectFrom(desktop.Args) };

		base.OnFrameworkInitializationCompleted();
	}

	/// <summary>The project file Explorer passed on double-click (the association's "%1").</summary>
	private static string? StartupProjectFrom(string[]? args)
	{
		return args?.FirstOrDefault(a => a.EndsWith(OverlayProject.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(a));
	}
}
