using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Layout;

namespace OsmoOverlay.Gui;

public sealed partial class MessageLinks : StackPanel
{
	public MessageLinks()
	{
		Spacing = 8;
		IsVisible = false;
	}

	public string SetMessage(string message)
	{
		Children.Clear();
		string displayMessage = message;
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (Match match in UrlPattern().Matches(message))
		{
			string url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
			if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !seen.Add(url)) continue;
			displayMessage = displayMessage.Replace($" ({url})", "", StringComparison.Ordinal);

			string label = uri.Host.Equals("developer.dji.com", StringComparison.OrdinalIgnoreCase) ? Strings.Common_OpenDjiPortal : url;
			var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left };
			button.Click += (_, _) => AppUpdateFlow.OpenInBrowser(uri.AbsoluteUri);
			Children.Add(button);
		}

		IsVisible = Children.Count > 0;
		return displayMessage;
	}

	[GeneratedRegex("https?://[^\\s<>\"']+", RegexOptions.IgnoreCase)]
	private static partial Regex UrlPattern();
}
