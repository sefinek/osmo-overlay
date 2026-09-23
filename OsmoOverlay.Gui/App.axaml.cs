using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Gui;

public class App : Application
{
	public override void Initialize()
	{
		AvaloniaXamlLoader.Load(this);
	}

	public override void OnFrameworkInitializationCompleted()
	{
		// Every button handler is async void - without this, one unexpected exception in any of them
		// takes the whole app down with nothing in app.log. Logged through AppLogger.Error, which the
		// main window mirrors into its LOG panel, and marked handled so the window stays usable.
		Dispatcher.UIThread.UnhandledException += (_, e) =>
		{
			AppLogger.Error(e.Exception, $"Unexpected error: {e.Exception.Message}");
			e.Handled = true;
		};
		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			AppLogger.Error(e.Exception, $"Unobserved background task error: {e.Exception.GetBaseException().Message}");
			e.SetObserved();
		};
		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
		{
			if (e.ExceptionObject is Exception ex) AppLogger.Error(ex, $"Fatal error: {ex.Message}");
		};

		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
			desktop.MainWindow = new MainWindow();

		base.OnFrameworkInitializationCompleted();
	}
}
