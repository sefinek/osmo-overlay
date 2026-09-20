using Avalonia.Controls;

namespace OsmoOverlay.Gui;

/// <summary>
///     Non-modal, movable per-widget settings panel - the counterpart to the settings ⚙ on a placed
///     widget's canvas hover icons. Deliberately a separate window (not the Flyout this used to be):
///     anchored popup content covering the very widget it edits made it impossible to see the effect of a
///     change while making it. One instance is reused for the app's whole lifetime (see
///     MainWindow.GetOrCreateWidgetSettingsWindow) - closing it (its own titlebar X) hides it instead of
///     disposing, so ShowPanel always re-hosts a widget-type's settings panel under this same PanelHost
///     ContentControl instance, never a second, already-closed one.
/// </summary>
public partial class WidgetSettingsWindow : Window
{
	public WidgetSettingsWindow()
	{
		InitializeComponent();
	}

	/// <summary>
	///     Displays `panel` (one of MainWindow's XxxSettingsPanel Borders, still fully wired to its own
	///     controls/event handlers - only its visual parent changes here) under this window's own title.
	///     The first time a given panel is shown, it's still parented to its original, invisible spot in
	///     MainWindow's widget palette Grid - detach it from there before adopting it; every later call
	///     (switching between widgets, or reopening the same one) finds it already parented to PanelHost
	///     from the previous call, so the ContentControl's own same-owner content swap handles it.
	/// </summary>
	public void ShowPanel(string title, Control panel)
	{
		if (panel.Parent is Panel oldParent) oldParent.Children.Remove(panel);

		Title = $"{title} settings";
		TitleText.Text = title;
		panel.IsVisible = true;
		PanelHost.Content = panel;
		PanelScroll.Offset = default;
	}
}
