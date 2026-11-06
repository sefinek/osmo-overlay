using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Updates;

namespace OsmoOverlay.Gui;

/// <summary>What the exported film may and may not be used for, as ticks and crosses, and the terms of the maps in the export. Closes with true once confirmed.</summary>
internal sealed class ExportRightsWindow : Window
{
	private const string PolyFormUrl = "https://polyformproject.org/licenses/noncommercial/1.0.0/";

	public ExportRightsWindow(IReadOnlyList<ExportMap> maps, bool confirm)
	{
		Title = Strings.ExportNotice_Title;
		Width = 820;
		Height = 760;
		MinWidth = 600;
		MinHeight = 420;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Background = Palette.WindowBackground;
		Icon = AppAssets.Icon;

		var body = new StackPanel { Spacing = 14, Margin = new Thickness(24, 20, 24, 12) };
		body.Children.Add(new StackPanel
		{
			Spacing = 4,
			Children = { new TextBlock { Text = Strings.ExportNotice_Title, FontSize = 20, FontWeight = FontWeight.Bold }, Hint(Strings.ExportNotice_Intro) }
		});

		body.Children.Add(Card(Strings.ExportNotice_ProgramHeader,
			Row(true, Strings.ExportNotice_Allow_Private),
			Row(true, Strings.ExportNotice_Allow_Free),
			Row(false, Strings.ExportNotice_Deny_Monetized),
			Row(false, Strings.ExportNotice_Deny_Earning),
			Row(false, Strings.ExportNotice_Deny_Paid),
			Row(false, Strings.ExportNotice_Deny_Business),
			Hint(Strings.ExportNotice_ProgramNote)));

		body.Children.Add(Card(Strings.ExportNotice_MaterialsHeader, Hint(Strings.ExportNotice_MaterialsText)));

		var mapRows = new List<Control>();
		if (maps.Count == 0) mapRows.Add(Hint(Strings.ExportNotice_NoMaps));
		foreach (ExportMap map in maps) mapRows.Add(MapCard(map));
		body.Children.Add(Card(Strings.ExportNotice_MapsHeader, [.. mapRows]));

		body.Children.Add(Hint(Strings.ExportNotice_Footer));
		body.Children.Add(new WrapPanel
		{
			Orientation = Orientation.Horizontal,
			Children =
			{
				Link(Strings.ExportNotice_LicenseLink, PolyFormUrl),
				Link(Strings.ExportNotice_LicenseFileLink, AppUpdates.RepositoryUrl + "/blob/main/LICENSE")
			}
		});
		body.Children.Add(Hint(Strings.MapTerms_Disclaimer));

		var ok = new Button { Content = Strings.ExportNotice_Confirm, IsDefault = true, Classes = { "accent" } };
		ok.Click += (_, _) => Close(true);
		var buttons = new StackPanel
		{
			Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 8, 24, 16)
		};
		if (confirm)
		{
			var cancel = new Button { Content = Strings.Common_Cancel, IsCancel = true };
			cancel.Click += (_, _) => Close(false);
			buttons.Children.Add(cancel);
		}

		buttons.Children.Add(ok);

		var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
		var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
		root.Children.Add(scroll);
		Grid.SetRow(buttons, 1);
		root.Children.Add(buttons);
		Content = root;
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		if (e.Key == Key.Escape) Close(false);
	}

	private static Control MapCard(ExportMap map)
	{
		if (map.Provider is not { } provider)
		{
			return new StackPanel
			{
				Spacing = 4,
				Children = { new TextBlock { Text = map.Name, FontWeight = FontWeight.SemiBold }, Row(null, Strings.ExportNotice_CustomMap) }
			};
		}

		MapTerms terms = provider.Terms;
		string credit = terms.Credit == MapCreditRule.Briefly ? Strings.MapTerms_CreditBriefly : Strings.MapTerms_CreditWhileVisible;
		var panel = new StackPanel { Spacing = 4 };
		panel.Children.Add(new TextBlock { Text = provider.Name, FontWeight = FontWeight.SemiBold });
		panel.Children.Add(Row(Mark(terms.Video), VideoText(terms.Video)));
		panel.Children.Add(Row(Mark(terms.Commercial), $"{Strings.MapTerms_Commercial}: {ExportNotice.UseText(terms.Commercial)}"));
		panel.Children.Add(Row(null, $"{Strings.MapTerms_Credit}: {credit}"));
		if (terms.Note is { } note) panel.Children.Add(Hint(note));
		if (terms.ShareAlike) panel.Children.Add(Hint(Strings.ExportNotice_ShareAlike));
		panel.Children.Add(Link(Strings.MapTerms_ReadTerms, terms.TermsUrl));
		return panel;
	}

	private static bool? Mark(MapUse use)
	{
		return use switch
		{
			MapUse.Allowed => true,
			MapUse.NotAllowed => false,
			_ => null
		};
	}

	private static string VideoText(MapUse use)
	{
		return use switch
		{
			MapUse.Allowed => Strings.MapTerms_VideoAllowed,
			MapUse.NotAllowed => Strings.MapTerms_VideoNotAllowed,
			MapUse.PaidPlanOnly => Strings.MapTerms_VideoPaid,
			MapUse.Conditional => Strings.MapTerms_VideoConditional,
			_ => Strings.MapTerms_VideoNotStated
		};
	}

	/// <summary>A tick (true), a cross (false), or a warning mark (null: conditional, not stated or not for the app to judge) before the text.</summary>
	private static Grid Row(bool? allowed, string text)
	{
		(Geometry icon, IBrush color) = allowed switch
		{
			true => (Icons.Check, Palette.Success),
			false => (Icons.Close, Palette.Danger),
			_ => (Icons.Warning, Palette.Warning)
		};
		var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
		row.Children.Add(new IconView { Data = icon, Foreground = color, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
		var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
		Grid.SetColumn(label, 1);
		row.Children.Add(label);
		return row;
	}

	private static Border Card(string header, params Control[] rows)
	{
		var panel = new StackPanel { Spacing = 8 };
		panel.Children.Add(new TextBlock { Text = header, FontWeight = FontWeight.SemiBold, FontSize = 15 });
		foreach (Control row in rows) panel.Children.Add(row);
		var card = new Border { Padding = new Thickness(14, 12), Child = panel };
		card.Classes.Add("card");
		return card;
	}

	private static TextBlock Hint(string text)
	{
		return new TextBlock { Text = text, Classes = { "hint" }, FontSize = 14, TextWrapping = TextWrapping.Wrap };
	}

	private static TextBlock Link(string text, string url)
	{
		var link = new TextBlock { Text = text, Classes = { "link" }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 16, 0) };
		link.PointerPressed += (_, _) => AppUpdateFlow.OpenInBrowser(url);
		return link;
	}
}
