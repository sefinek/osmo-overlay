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

	public ConfirmDialog()
	{
		InitializeComponent();
	}

	private ConfirmDialog(string title, string message, string confirmText, DialogKind kind, bool alert) : this()
	{
		Title = title;
		TitleText.Text = title;
		MessageText.Text = message;
		ConfirmButton.Content = confirmText;
		ConfirmButton.Classes.Add(ButtonClassFor(kind));
		KindDot.Background = DotBrushFor(kind);

		// Alert mode (ShowAsync) is a single-button "OK" notice, not a Cancel/Confirm choice - hide
		// Cancel so there's only one way out, matching a plain message box rather than leaving a
		// decision the caller never asked for.
		if (alert) CancelButton.IsVisible = false;
	}

	/// <summary>Shows a modal Cancel/Confirm prompt over `owner` and returns true only if Confirm was clicked.</summary>
	public static Task<bool> AskAsync(Window owner, string title, string message, string confirmText = "Confirm",
		DialogKind kind = DialogKind.Neutral)
	{
		return new ConfirmDialog(title, message, confirmText, kind, false).ShowDialog<bool>(owner);
	}

	/// <summary>Shows a modal single-button notice over `owner` - no Cancel/decision, just an acknowledgement.</summary>
	public static Task ShowAsync(Window owner, string title, string message, string closeText = "OK",
		DialogKind kind = DialogKind.Info)
	{
		return new ConfirmDialog(title, message, closeText, kind, true).ShowDialog(owner);
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

	private void OnCancelClick(object? sender, RoutedEventArgs e)
	{
		Close(false);
	}

	private void OnConfirmClick(object? sender, RoutedEventArgs e)
	{
		Close(true);
	}
}
