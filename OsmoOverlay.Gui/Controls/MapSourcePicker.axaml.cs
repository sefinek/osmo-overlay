using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OsmoOverlay.Core.Mapping;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     Which tile server one map uses (a Map widget, the route overview card): the provider belongs to that map and is
///     reported through SelectionChanged; everything else - the API key, the custom server's URL and credit, whether the
///     credit is drawn - is shared by every map, so it's written to the settings right here (SharedChanged) and shows up
///     the same in every picker. The list is MapProviders' - one for the whole app.
/// </summary>
public sealed partial class MapSourcePicker : UserControl
{
	private static readonly List<ProviderOption> Options =
	[
		.. MapProviders.BuiltIn.Select(p => new ProviderOption(p.Id, p.Name)),
		new(MapProviders.CustomId, Strings.MapSource_Custom)
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
		ProviderCombo.SelectedItem = Options.FirstOrDefault(o => o.Id == providerId) ?? Options[0];
		CustomUrlBox.Text = settings.CustomMapUrlTemplate;
		CustomAttributionBox.Text = settings.CustomMapAttribution;
		ShowAttributionCheck.IsChecked = settings.MapShowAttribution;
		ShowSelected(settings);
		_loading = false;
	}

	private string SelectedId => (ProviderCombo.SelectedItem as ProviderOption)?.Id ?? MapProviders.DefaultId;

	private MapSources CurrentSources(OverlaySettings settings)
	{
		return new MapSources(settings.MapApiKeys, CustomUrlBox.Text, CustomAttributionBox.Text, settings.MapShowAttribution);
	}

	/// <summary>Shows what the selected provider needs: the custom URL, a key, its credit.</summary>
	private void ShowSelected(OverlaySettings settings)
	{
		bool custom = SelectedId == MapProviders.CustomId;
		MapSources sources = CurrentSources(settings);
		string? group = sources.KeyGroupOf(SelectedId);

		CustomUrlLabel.IsVisible = custom;
		CustomUrlBox.IsVisible = custom;
		ApiKeyLabel.IsVisible = group is not null;
		ApiKeyBox.IsVisible = group is not null;
		SharedHint.IsVisible = group is not null || custom;
		ApiKeyBox.Text = sources.ApiKey(group);
		UpdateKeyValidation(group);

		bool show = ShowAttributionCheck.IsChecked == true;
		CustomAttributionBox.IsVisible = custom && show;
		AttributionText.IsVisible = !custom && show;
		AttributionText.Text = string.Format(Strings.MapSource_CreditShown, sources.Attribution(SelectedId));
	}

	// CARTO's own basemap keys follow a fixed "<id>_<id>_<n>_<24 hex chars>" shape.
	[GeneratedRegex(@"^[a-z0-9]+_[a-z0-9]+_\d+_[0-9a-f]{24}$", RegexOptions.IgnoreCase)]
	private static partial Regex CartoApiKeyPattern();

	/// <summary>
	///     A soft hint for a key that doesn't look like a CARTO one (a render is still attempted - only CARTO's server
	///     really validates it). Not for the custom server: its template could be any provider with its own key format.
	/// </summary>
	private void UpdateKeyValidation(string? group)
	{
		string? key = ApiKeyBox.Text;
		ApiKeyHint.IsVisible = group == MapProviders.CartoKeyGroup && !string.IsNullOrWhiteSpace(key) && !CartoApiKeyPattern().IsMatch(key.Trim());
	}

	private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_loading || ProviderCombo.SelectedItem is null) return;

		ShowSelected(OverlaySettingsStore.Load());
		SelectionChanged?.Invoke(SelectedId);
	}

	private void OnShowAttributionClick(object? sender, RoutedEventArgs e)
	{
		SaveShared(OverlaySettingsStore.Load() with { MapShowAttribution = ShowAttributionCheck.IsChecked == true });
		ShowSelected(OverlaySettingsStore.Load());
	}

	private void OnApiKeyLostFocus(object? sender, RoutedEventArgs e)
	{
		OverlaySettings settings = OverlaySettingsStore.Load();
		if (CurrentSources(settings).KeyGroupOf(SelectedId) is not { } group) return;

		string key = ApiKeyBox.Text?.Trim() ?? "";
		Dictionary<string, string> keys = settings.MapApiKeys is { } existing ? new Dictionary<string, string>(existing) : [];
		if (key.Length == 0) keys.Remove(group);
		else keys[group] = key;

		SaveShared(settings with { MapApiKeys = keys.Count == 0 ? null : keys });
		UpdateKeyValidation(group);
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
