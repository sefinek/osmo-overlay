using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     Which tile server one map uses (a Map widget, the route overview card): the provider belongs to that map and is
///     reported through SelectionChanged; everything else - the API key, the custom server's URL, credit and its credit's
///     rule - is shared by every map, so it's written to the settings right here (SharedChanged) and shows up the same in
///     every picker. The list is MapProviders' - one for the whole app - and so are the terms shown for each (MapTerms).
/// </summary>
public sealed partial class MapSourcePicker : UserControl
{
	private static readonly List<ProviderOption> Options =
	[
		new(MapProviders.AutoSatelliteId, Strings.MapSource_AutoSatellite),
		new(MapProviders.StreetsAutoId, Strings.MapSource_AutoStreets),
		.. MapProviders.BuiltIn.Where(p => p.Terms.Video != MapUse.NotAllowed).Select(p => new ProviderOption(p.Id, p.Name)),
		new(MapProviders.CustomId, Strings.MapSource_Custom),
		.. MapProviders.BuiltIn.Where(p => p.Terms.Video == MapUse.NotAllowed).Select(p => new ProviderOption(p.Id, p.Name))
	];

	private bool _loading;

	public MapSourcePicker()
	{
		InitializeComponent();
		ProviderCombo.ItemsSource = Options;
	}

	/// <summary>The provider id, after the user picked another one.</summary>
	public event Action<string>? SelectionChanged;

	/// <summary>A key or the custom server changed in the settings - the preview fetches again what it affects.</summary>
	public event Action? SharedChanged;

	public void Load(string? providerId, OverlaySettings settings)
	{
		_loading = true;
		string shown = providerId ?? settings.DefaultMapProvider ?? MapProviders.AutoSatelliteId;
		ProviderCombo.SelectedItem = Options.FirstOrDefault(o => o.Id == shown) ?? Options[0];
		CustomUrlBox.Text = settings.CustomMapUrlTemplate;
		CustomAttributionBox.Text = settings.CustomMapAttribution;
		CustomCreditBrieflyCheck.IsChecked = settings.CustomMapCreditBriefly;
		ShowSelected(settings);
		_loading = false;
	}

	private string SelectedId => (ProviderCombo.SelectedItem as ProviderOption)?.Id ?? MapProviders.DefaultId;

	private MapSources CurrentSources(OverlaySettings settings)
	{
		return new MapSources(settings.MapApiKeys, CustomUrlBox.Text, CustomAttributionBox.Text, CustomCreditBrieflyCheck.IsChecked == true);
	}

	private bool IsBestAvailable => SelectedId is MapProviders.AutoSatelliteId or MapProviders.StreetsAutoId;

	/// <summary>
	///     Top to bottom: what the pick means (a best-available one says what it uses now), whether that map may go in a film,
	///     then what it needs - the custom server's URL, a key.
	/// </summary>
	private void ShowSelected(OverlaySettings settings)
	{
		bool best = IsBestAvailable;
		bool custom = SelectedId == MapProviders.CustomId;
		MapSources sources = CurrentSources(settings);
		string? group = KeyGroupShown(sources);

		AutoHint.IsVisible = best;
		AutoHint.Text = SelectedId == MapProviders.StreetsAutoId ? Strings.MapSource_AutoStreetsDescription : Strings.MapSource_AutoDescription;
		ShowStatus(sources, best, custom);

		CustomUrlLabel.IsVisible = custom;
		CustomUrlBox.IsVisible = custom;

		ApiKeyHeader.IsVisible = group is not null;
		ApiKeyBox.IsVisible = group is not null;
		ApiKeyLabel.Text = group is not null && KeyGroupNames.TryGetValue(group, out string? owner)
			? string.Format(Strings.MapSource_KeyFor, owner)
			: Strings.MapSource_ApiKey;
		ApiKeyPageLink.IsVisible = group is not null && MapProviders.KeyPages.ContainsKey(group);
		// Esri's key gets the walk through its wizard; the others only their sign-up page, and the link says so.
		ApiKeyPageLink.Text = group == MapProviders.EsriKeyGroup ? Strings.MapSource_GetKeyShort
			: group is not null && KeyGroupNames.TryGetValue(group, out string? site) ? string.Format(Strings.MapSource_KeyPage, site) : "";
		ApiKeyBox.Text = sources.ApiKey(group);
		SharedHint.IsVisible = group is not null || custom;
		UpdateKeyValidation(group);

		CustomAttributionLabel.IsVisible = custom;
		CustomAttributionBox.IsVisible = custom;
		CustomCreditBrieflyCheck.IsVisible = custom;
	}

	private static readonly Dictionary<string, string> KeyGroupNames = new()
	{
		[MapProviders.EsriKeyGroup] = "Esri",
		[MapProviders.MapTilerKeyGroup] = "MapTiler",
		[MapProviders.ThunderforestKeyGroup] = "Thunderforest",
		[MapProviders.CartoKeyGroup] = "CARTO"
	};

	/// <summary>
	///     One card for the map really used: may it go in a film (the answer people pick a map by), what's used now or the
	///     provider's own note, the rest of its terms in one line, and the link to them. A custom server's terms are the user's.
	/// </summary>
	private void ShowStatus(MapSources sources, bool best, bool custom)
	{
		StatusPanel.Children.Clear();
		if (custom)
		{
			StatusPanel.Children.Add(Hint(Strings.MapTerms_Custom));
			return;
		}

		string resolved = sources.Resolve(SelectedId);
		MapProvider provider = MapProviders.Get(resolved);
		MapTerms terms = provider.Terms;
		bool publicEsri = resolved is MapProviders.EsriPublicId or MapProviders.EsriStreetsPublicId;

		(IBrush color, Geometry icon, string verdict) = terms.Video switch
		{
			MapUse.Allowed => (Palette.Success, Icons.Check, Strings.MapTerms_VideoAllowed),
			MapUse.NotAllowed => (Palette.Danger, Icons.Warning, Strings.MapTerms_VideoNotAllowed),
			MapUse.PaidPlanOnly => (Palette.Warning, Icons.Warning, Strings.MapTerms_VideoPaid),
			MapUse.Conditional => (Palette.Warning, Icons.Exclamation, Strings.MapTerms_VideoConditional),
			_ => (Palette.TextMuted, Icons.Info, Strings.MapTerms_VideoNotStated)
		};
		var headline = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
		headline.Children.Add(new IconView
		{
			Data = icon, Foreground = color, Width = 16, Height = 16, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
		});
		var verdictText = new TextBlock { Text = verdict, Foreground = color, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
		Grid.SetColumn(verdictText, 1);
		headline.Children.Add(verdictText);
		TextBlock termsLink = Link(Strings.MapTerms_ReadTerms, terms.TermsUrl);
		termsLink.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
		Grid.SetColumn(termsLink, 2);
		headline.Children.Add(termsLink);

		var panel = new StackPanel { Spacing = 6 };
		panel.Children.Add(headline);

		string? detail = best
			? publicEsri ? Strings.MapSource_BestNoKey : string.Format(Strings.MapSource_BestUses, provider.Name)
			: terms.Note;
		if (detail is not null) panel.Children.Add(Hint(detail));

		var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, RowSpacing = 3 };
		void Fact(string label, string value, IBrush? color = null)
		{
			int row = facts.RowDefinitions.Count;
			facts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
			var name = new TextBlock { Text = label, Classes = { "hint" } };
			var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
			if (color is not null) text.Foreground = color;
			Grid.SetRow(name, row);
			Grid.SetRow(text, row);
			Grid.SetColumn(text, 1);
			facts.Children.Add(name);
			facts.Children.Add(text);
		}

		Fact(Strings.MapTerms_Commercial, UseText(terms.Commercial), terms.Commercial switch
		{
			MapUse.Allowed => Palette.Success,
			MapUse.NotAllowed => Palette.Danger,
			MapUse.PaidPlanOnly or MapUse.Conditional => Palette.Warning,
			_ => null
		});
		Fact(Strings.MapTerms_Credit, terms.Credit == MapCreditRule.Briefly ? Strings.MapTerms_CreditBriefly : Strings.MapTerms_CreditWhileVisible);
		if (terms.Region is { } region) Fact(Strings.MapTerms_Coverage, region.Name);
		panel.Children.Add(facts);

		if (publicEsri && !best) panel.Children.Add(Link(Strings.MapSource_GetEsriKey, MapProviders.KeyPages[MapProviders.EsriKeyGroup]));

		var card = new Border { Padding = new Avalonia.Thickness(12, 10), Child = panel };
		card.Classes.Add("card");
		StatusPanel.Children.Add(card);
	}

	private static TextBlock Link(string text, string url)
	{
		var link = new TextBlock { Text = text, Classes = { "link" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
		link.PointerPressed += (_, _) => AppUpdateFlow.OpenInBrowser(url);
		return link;
	}

	private static TextBlock Hint(string text)
	{
		return new TextBlock { Text = text, Classes = { "hint" }, TextWrapping = TextWrapping.Wrap };
	}

	private static string UseText(MapUse use)
	{
		return use switch
		{
			MapUse.Allowed => Strings.MapTerms_Allowed,
			MapUse.PaidPlanOnly => Strings.MapTerms_PaidPlanOnly,
			MapUse.NotAllowed => Strings.MapTerms_NotAllowed,
			MapUse.Conditional => Strings.MapTerms_Conditional,
			_ => Strings.MapTerms_NotStated
		};
	}

	// CARTO's own basemap keys follow a fixed "<id>_<id>_<n>_<24 hex chars>" shape.
	[GeneratedRegex(@"^[a-z0-9]+_[a-z0-9]+_\d+_[0-9a-f]{24}$", RegexOptions.IgnoreCase)]
	private static partial Regex CartoApiKeyPattern();

	/// <summary>
	///     A key the provider needs and nobody gave (nothing is fetched then - MapSources.MissesApiKey), or a soft hint for
	///     a key that doesn't look like a CARTO one (a render is still attempted - only CARTO's server
	///     really validates it). Not for the other providers: their keys have no published shape.
	/// </summary>
	private void UpdateKeyValidation(string? group)
	{
		string? key = ApiKeyBox.Text;
		// A best-available pick works without a key too (Esri's public service), so a missing one is no error there.
		ApiKeyMissingHint.IsVisible = group is not null && string.IsNullOrWhiteSpace(key) && !IsBestAvailable;
		ApiKeyHint.IsVisible = group == MapProviders.CartoKeyGroup && !string.IsNullOrWhiteSpace(key) && !CartoApiKeyPattern().IsMatch(key.Trim());
	}

	private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_loading || ProviderCombo.SelectedItem is null) return;

		ShowSelected(OverlaySettingsStore.Load());
		SelectionChanged?.Invoke(SelectedId);
	}

	private void OnApiKeyPageClick(object? sender, PointerPressedEventArgs e)
	{
		if (KeyGroupShown(CurrentSources(OverlaySettingsStore.Load())) is not { } group) return;

		if (group == MapProviders.EsriKeyGroup) FlyoutBase.ShowAttachedFlyout(ApiKeyPageLink);
		else if (MapProviders.KeyPages.TryGetValue(group, out string? url)) AppUpdateFlow.OpenInBrowser(url);
	}

	private void OnApiKeyLostFocus(object? sender, RoutedEventArgs e)
	{
		OverlaySettings settings = OverlaySettingsStore.Load();
		if (KeyGroupShown(CurrentSources(settings)) is not { } group) return;

		string key = ApiKeyBox.Text?.Trim() ?? "";
		// Leaving the box without a change saves nothing - a save makes the preview fetch its maps again.
		if (key == (settings.MapApiKeys?.GetValueOrDefault(group)?.Trim() ?? "")) return;

		Dictionary<string, string> keys = settings.MapApiKeys is { } existing ? new Dictionary<string, string>(existing) : [];
		if (key.Length == 0) keys.Remove(group);
		else keys[group] = key;

		SaveShared(settings with { MapApiKeys = keys.Count == 0 ? null : keys });
		ShowSelected(OverlaySettingsStore.Load());
	}

	// The best-available picks take Esri's key - the one that makes them sharp and allowed in a film - whatever they resolve to now.
	private string? KeyGroupShown(MapSources sources)
	{
		return SelectedId is MapProviders.AutoSatelliteId or MapProviders.StreetsAutoId ? MapProviders.EsriKeyGroup : sources.KeyGroupOf(SelectedId);
	}

	private void OnCustomUrlLostFocus(object? sender, RoutedEventArgs e)
	{
		OverlaySettings settings = OverlaySettingsStore.Load();
		string? url = string.IsNullOrWhiteSpace(CustomUrlBox.Text) ? null : CustomUrlBox.Text.Trim();
		SaveShared(settings with { CustomMapUrlTemplate = url });
		ShowSelected(OverlaySettingsStore.Load());
	}

	private void OnCustomAttributionLostFocus(object? sender, RoutedEventArgs e)
	{
		string? text = string.IsNullOrWhiteSpace(CustomAttributionBox.Text) ? null : CustomAttributionBox.Text.Trim();
		SaveShared(OverlaySettingsStore.Load() with { CustomMapAttribution = text });
	}

	private void OnCustomCreditBrieflyClick(object? sender, RoutedEventArgs e)
	{
		SaveShared(OverlaySettingsStore.Load() with { CustomMapCreditBriefly = CustomCreditBrieflyCheck.IsChecked == true });
	}

	private void SaveShared(OverlaySettings settings)
	{
		if (settings == OverlaySettingsStore.Load()) return;

		OverlaySettingsStore.Save(settings);
		SharedChanged?.Invoke();
	}

	private sealed record ProviderOption(string Id, string Name)
	{
		public override string ToString()
		{
			return Name;
		}
	}
}
