using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace OsmoOverlay.Gui;

/// <summary>
///     A click on something that can't take the keyboard focus itself (empty space, a card, the preview, a label) clears
///     the focus - Avalonia leaves it in the last TextBox, whose caret keeps blinking and which keeps the shortcuts that
///     step aside for a focused TextBox switched off. Tunnelling, so a handler of that click can still focus something.
/// </summary>
internal static class FocusRelease
{
	/// <summary>From App.Initialize, before any window is created - covers every window and popup.</summary>
	public static void Configure()
	{
		InputElement.PointerPressedEvent.AddClassHandler<TopLevel>(OnPointerPressed, RoutingStrategies.Tunnel, true);
	}

	private static void OnPointerPressed(TopLevel topLevel, PointerPressedEventArgs e)
	{
		if (topLevel.FocusManager is not { } focus || focus.GetFocusedElement() is null || e.Source is not Visual source) return;

		// A focusable control takes the focus on its own; a scrollbar is how the focused field's own content is reached.
		bool handledElsewhere = source.GetSelfAndVisualAncestors()
			.TakeWhile(v => v != topLevel)
			.Any(v => v is ScrollBar || v is InputElement { Focusable: true, IsEffectivelyEnabled: true });
		if (!handledElsewhere) focus.Focus(null);
	}
}
