using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using OsmoOverlay.Core.Mapping;

namespace OsmoOverlay.Gui;

/// <summary>
///     How to make the free Esri key, step by step as ArcGIS's own wizard goes - in the first-run window's Map step and
///     next to the map picker's Esri key field.
/// </summary>
public sealed partial class EsriKeyGuide : UserControl
{
	private const string GuideUrl =
		"https://developers.arcgis.com/documentation/security-and-authentication/api-key-authentication/tutorials/create-an-api-key/location-platform/";

	public EsriKeyGuide()
	{
		InitializeComponent();
		string[] lines = Strings.EsriKeyGuide_Steps.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < lines.Length; i++)
			Steps.Children.Add(Step(i + 1, lines[i]));
	}

	private static Grid Step(int number, string line)
	{
		int dot = line.IndexOf(". ", StringComparison.Ordinal);
		string text = dot > 0 && line[..dot].All(char.IsDigit) ? line[(dot + 2)..] : line;
		var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*") };
		grid.Children.Add(new TextBlock
		{
			Text = $"{number}.", FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Palette.Accent,
			HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
		});
		var body = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
		Grid.SetColumn(body, 1);
		grid.Children.Add(body);
		return grid;
	}

	private void OnOpenArcGisClick(object? sender, RoutedEventArgs e)
	{
		AppUpdateFlow.OpenInBrowser(MapProviders.KeyPages[MapProviders.EsriKeyGroup]);
	}

	private void OnGuideClick(object? sender, RoutedEventArgs e)
	{
		AppUpdateFlow.OpenInBrowser(GuideUrl);
	}
}
