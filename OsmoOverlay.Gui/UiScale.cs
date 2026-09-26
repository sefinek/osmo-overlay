using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     The interface scale from Settings (OverlaySettings.InterfaceScale), applied once at startup. On Linux it goes to
///     Avalonia's X11 backend as AVALONIA_GLOBAL_SCALE_FACTOR, so RenderScaling itself includes it. Windows and macOS have
///     no such switch, so there every window's content is wrapped in a LayoutTransformControl (Factor) with its own
///     VisualLayerManager, and popups (menus, dropdowns, flyouts, tooltips) are drawn in that manager's overlay layer
///     instead of native popup windows - otherwise they'd stay at 100%.
/// </summary>
internal static class UiScale
{
	private const string ScaleFactorVariable = "AVALONIA_GLOBAL_SCALE_FACTOR";
	private const string ScaleMarker = "UiScale";

	private static double _configured = 1.0;

	/// <summary>The app-level scale on top of RenderScaling - 1 on Linux, where the backend applies it.</summary>
	public static double Factor { get; private set; } = 1.0;

	/// <summary>Before the AppBuilder starts - the X11 backend reads the variable while initializing.</summary>
	public static void Initialize()
	{
		var scale = OverlaySettingsStore.Load().InterfaceScale;
		if (!double.IsFinite(scale) || scale < 0.5 || scale > 3.0 || Math.Abs(scale - 1.0) < 0.001) return;

		_configured = scale;
		if (OperatingSystem.IsLinux())
			Environment.SetEnvironmentVariable(ScaleFactorVariable, scale.ToString(CultureInfo.InvariantCulture));
		else
			Factor = scale;
	}

	/// <summary>
	///     From App.Initialize, before any window or popup is created. The overlay-layer switches go in as app styles -
	///     both properties already carry metadata for the types they'd be overridden on, so OverrideDefaultValue throws.
	///     A tooltip's popup takes its value from the control's ToolTip.ShouldUseOverlayLayer, hence the second style.
	/// </summary>
	public static void Configure(Application app)
	{
		if (_configured != 1.0) AppLogger.Info($"Interface scale: {_configured * 100:0}%");
		if (Factor == 1.0) return;

		app.Styles.Add(new Style(x => x.OfType<Popup>()) { Setters = { new Setter(Popup.ShouldUseOverlayLayerProperty, true) } });
		app.Styles.Add(new Style(x => x.Is<Control>()) { Setters = { new Setter(ToolTip.ShouldUseOverlayLayerProperty, true) } });
		Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Apply(window));
	}

	/// <summary>Device pixels per layout unit of a control inside a window: RenderScaling times the app-level scale.</summary>
	public static double DeviceScaling(TopLevel? topLevel)
	{
		return (topLevel?.RenderScaling ?? 1.0) * Factor;
	}

	private static void Apply(Window window)
	{
		if (window.Content is not Control content || content is LayoutTransformControl { Tag: ScaleMarker }) return;

		window.Content = null;
		window.Content = new LayoutTransformControl
		{
			Tag = ScaleMarker,
			LayoutTransform = new ScaleTransform(Factor, Factor),
			Child = new VisualLayerManager { Child = content }
		};

		// Sizes from XAML are layout units of the unscaled content - scaled with it, but never past the screen.
		Screen? screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
		Size? workArea = screen is null ? null : ToDips(screen);
		window.Width = Scaled(window.Width, workArea?.Width);
		window.Height = Scaled(window.Height, workArea?.Height);
		window.MinWidth = Scaled(window.MinWidth, workArea?.Width);
		window.MinHeight = Scaled(window.MinHeight, workArea?.Height);
	}

	private static Size ToDips(Screen screen)
	{
		return new Size(screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling);
	}

	private static double Scaled(double value, double? limit)
	{
		if (!double.IsFinite(value) || value <= 0) return value;

		var scaled = value * Factor;
		return limit is { } max ? Math.Min(scaled, max) : scaled;
	}
}
