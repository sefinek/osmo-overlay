using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OsmoOverlay.Gui;

public partial class ConfirmDialog : Window
{
	public ConfirmDialog()
	{
		InitializeComponent();
	}

	private ConfirmDialog(string title, string message, string confirmText, bool destructive) : this()
	{
		Title = title;
		TitleText.Text = title;
		MessageText.Text = message;
		ConfirmButton.Content = confirmText;
		ConfirmButton.Classes.Add(destructive ? "danger" : "accent");
	}

	/// <summary>Shows a modal Cancel/Confirm prompt over `owner` and returns true only if Confirm was clicked.</summary>
	public static Task<bool> AskAsync(Window owner, string title, string message, string confirmText = "Confirm",
		bool destructive = false)
	{
		return new ConfirmDialog(title, message, confirmText, destructive).ShowDialog<bool>(owner);
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
