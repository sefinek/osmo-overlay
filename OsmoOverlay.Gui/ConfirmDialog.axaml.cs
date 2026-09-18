using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace OsmoOverlay.Gui;

/// <summary>Severity/intent of a ConfirmDialog - drives the primary button's color and the small dot next to the title.</summary>
public enum DialogKind
{
	Neutral,
	Info,
	Success,
	Warning,
	Danger
}

public partial class ConfirmDialog : Window
{
	private static readonly IBrush NeutralDotBrush = new SolidColorBrush(Color.Parse("#5A5A62"));
	private static readonly IBrush InfoDotBrush = new SolidColorBrush(Color.Parse("#3E9EFF"));
	private static readonly IBrush SuccessDotBrush = new SolidColorBrush(Color.Parse("#3EBE6E"));
	private static readonly IBrush WarningDotBrush = new SolidColorBrush(Color.Parse("#E5A83E"));
	private static readonly IBrush DangerDotBrush = new SolidColorBrush(Color.Parse("#E5484D"));

	private readonly Func<Task>? _onConfirm;
	private readonly string? _workingText;
	private readonly Action? _onSecondary;

	public ConfirmDialog()
	{
		InitializeComponent();
	}

	private ConfirmDialog(string title, string message, string confirmText, DialogKind kind, bool alert,
		string? windowTitle, Func<Task>? onConfirm, string? workingText, string? secondaryText, Action? onSecondary) : this()
	{
		// windowTitle (the OS window chrome/taskbar text) is deliberately separate from `title` (the
		// heading shown in the body) - a caller that shows several outcomes for the same action (e.g.
		// ToolsWindow's color tag fixer) wants a stable window title across all of them while the
		// heading changes per outcome, instead of both saying the same thing twice on screen.
		Title = windowTitle ?? title;
		TitleText.Text = title;
		MessageText.Text = message;
		ConfirmButton.Content = confirmText;
		ConfirmButton.Classes.Add(ButtonClassFor(kind));
		KindDot.Background = DotBrushFor(kind);
		KindGlyph.Text = GlyphFor(kind);
		_onConfirm = onConfirm;
		_workingText = workingText;
		_onSecondary = onSecondary;

		// Alert mode (ShowAsync) is normally a single-button "OK" notice, not a Cancel/Confirm choice,
		// so Cancel is hidden - unless the caller gave it a secondary action (e.g. "Show in folder"),
		// in which case it's repurposed into that action button instead of a Cancel.
		if (onSecondary is not null)
		{
			CancelButton.Content = secondaryText ?? "Cancel";
		}
		else if (alert)
		{
			CancelButton.IsVisible = false;
		}
	}

	/// <summary>
	///     Shows a modal Cancel/Confirm prompt over `owner` and returns true only if Confirm was clicked.
	///     If `onConfirm` is given, clicking Confirm disables both buttons, switches the Confirm button's
	///     label to `workingText`, awaits `onConfirm`, and only then closes - so a caller that runs a
	///     slow operation from Confirm (e.g. an ffmpeg pass) can show its own progress on this dialog
	///     instead of the dialog vanishing immediately and the caller having to signal progress elsewhere.
	/// </summary>
	public static Task<bool> AskAsync(Window owner, string title, string message, string confirmText = "Confirm",
		DialogKind kind = DialogKind.Neutral, string? windowTitle = null, Func<Task>? onConfirm = null, string? workingText = null)
	{
		return new ConfirmDialog(title, message, confirmText, kind, false, windowTitle, onConfirm, workingText, null, null).ShowDialog<bool>(owner);
	}

	/// <summary>
	///     Shows a modal single-button notice over `owner` - no Cancel/decision, just an acknowledgement.
	///     If `onSecondary` is given, a second button labeled `secondaryText` appears next to the close
	///     button (e.g. "Show in folder") - clicking it runs the action without closing the dialog, so
	///     the caller can still read the rest of the message afterwards.
	/// </summary>
	public static Task ShowAsync(Window owner, string title, string message, string closeText = "OK",
		DialogKind kind = DialogKind.Info, string? windowTitle = null, string? secondaryText = null, Action? onSecondary = null)
	{
		return new ConfirmDialog(title, message, closeText, kind, true, windowTitle, null, null, secondaryText, onSecondary).ShowDialog(owner);
	}

	private static string ButtonClassFor(DialogKind kind)
	{
		return kind switch
		{
			DialogKind.Danger => "danger",
			DialogKind.Warning => "warning",
			DialogKind.Success => "success",
			_ => "accent"
		};
	}

	private static IBrush DotBrushFor(DialogKind kind)
	{
		return kind switch
		{
			DialogKind.Info => InfoDotBrush,
			DialogKind.Success => SuccessDotBrush,
			DialogKind.Warning => WarningDotBrush,
			DialogKind.Danger => DangerDotBrush,
			_ => NeutralDotBrush
		};
	}

	private static string GlyphFor(DialogKind kind)
	{
		return kind switch
		{
			DialogKind.Success => "✓",
			DialogKind.Warning => "!",
			DialogKind.Danger => "✕",
			DialogKind.Info => "i",
			_ => "i"
		};
	}

	private void OnCancelClick(object? sender, RoutedEventArgs e)
	{
		if (_onSecondary is not null)
		{
			_onSecondary();
			return;
		}

		Close(false);
	}

	private async void OnConfirmClick(object? sender, RoutedEventArgs e)
	{
		if (_onConfirm is not null)
		{
			CancelButton.IsEnabled = false;
			ConfirmButton.IsEnabled = false;
			ConfirmButton.Content = _workingText ?? "Working...";
			await _onConfirm();
		}

		Close(true);
	}
}
