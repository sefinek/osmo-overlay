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
	private static readonly IBrush NeutralDotBrush = Palette.TextMuted;
	private static readonly IBrush InfoDotBrush = Palette.Accent;
	private static readonly IBrush SuccessDotBrush = Palette.Success;
	private static readonly IBrush WarningDotBrush = Palette.Warning;
	private static readonly IBrush DangerDotBrush = Palette.Danger;

	private readonly Func<Task>? _onConfirm;
	private readonly Action? _onExtra;
	private readonly Action? _onSecondary;
	private readonly string? _workingText;

	public ConfirmDialog()
	{
		InitializeComponent();
	}

	private ConfirmDialog(string title, string message, string confirmText, DialogKind kind, bool alert,
		string? windowTitle, Func<Task>? onConfirm, string? workingText, string? secondaryText, Action? onSecondary,
		string? extraText, Action? onExtra) : this()
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
		KindIcon.Data = IconFor(kind);
		_onConfirm = onConfirm;
		_workingText = workingText;
		_onSecondary = onSecondary;
		_onExtra = onExtra;

		// Alert mode (ShowAsync) is normally a single-button "OK" notice, not a Cancel/Confirm choice,
		// so Cancel is hidden - unless the caller gave it a secondary action (e.g. "Show in folder"),
		// in which case it's repurposed into that action button instead of a Cancel.
		if (onSecondary is not null)
			CancelButton.Content = secondaryText ?? "Cancel";
		else if (alert) CancelButton.IsVisible = false;

		if (onExtra is not null)
		{
			ExtraButton.Content = extraText ?? "Extra";
			ExtraButton.IsVisible = true;
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
		return new ConfirmDialog(title, message, confirmText, kind, false, windowTitle, onConfirm, workingText, null, null, null, null).ShowDialog<bool>(owner);
	}

	/// <summary>
	///     Shows a modal single-button notice over `owner` - no Cancel/decision, just an acknowledgement.
	///     If `onSecondary`/`onExtra` are given, one or two extra buttons appear next to the close button
	///     (e.g. "Show in folder", "Compare files") - clicking either runs its action without closing the
	///     dialog, so the caller can still read the rest of the message or click another action afterwards.
	/// </summary>
	public static Task ShowAsync(Window owner, string title, string message, string closeText = "OK",
		DialogKind kind = DialogKind.Info, string? windowTitle = null, string? secondaryText = null, Action? onSecondary = null,
		string? extraText = null, Action? onExtra = null)
	{
		return new ConfirmDialog(title, message, closeText, kind, true, windowTitle, null, null, secondaryText, onSecondary, extraText, onExtra).ShowDialog(owner);
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

	private static Geometry IconFor(DialogKind kind)
	{
		return kind switch
		{
			DialogKind.Success => Icons.Check,
			DialogKind.Warning => Icons.Exclamation,
			DialogKind.Danger => Icons.Close,
			_ => Icons.Info
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

	private void OnExtraClick(object? sender, RoutedEventArgs e)
	{
		_onExtra?.Invoke();
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
