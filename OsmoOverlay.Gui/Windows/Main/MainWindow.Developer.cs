using Avalonia.Input;

namespace OsmoOverlay.Gui;

/// <summary>Ctrl+Shift+D opens the developer tools (DeveloperWindow) - one at a time, beside the main window rather than over it.</summary>
public partial class MainWindow
{
	private DeveloperWindow? _developerWindow;

	private void WireDeveloperWindow()
	{
		KeyDown += (_, e) =>
		{
			if (e.Key != Key.D || e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift)) return;

			e.Handled = true;
			if (_developerWindow is not null)
			{
				_developerWindow.Activate();
				return;
			}

			_developerWindow = new DeveloperWindow(this);
			_developerWindow.Closed += (_, _) => _developerWindow = null;
			_developerWindow.Show(this);
		};
	}
}
