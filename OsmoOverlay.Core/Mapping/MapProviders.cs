using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Core.Mapping;

/// <summary>
///     One tile server. Providers that share an account share its API key (KeyGroup: every CARTO style takes the same
///     key); null means the URL has no "{api_key}" placeholder.
/// </summary>
public sealed record MapProvider(string Id, string Name, string UrlTemplate, string Attribution, string? KeyGroup = null)
{
	public bool NeedsApiKey => KeyGroup is not null;
}

/// <summary>
///     The one list of tile servers every map-based widget (the Map widget, the route overview start card) picks from.
///     "custom" is a hand-entered URL kept once in OverlaySettings (CustomMapUrlTemplate) - see MapSources.
/// </summary>
public static class MapProviders
{
	public const string DefaultId = "esri";
	public const string CustomId = "custom";
	public const string CartoKeyGroup = "carto";

	public static readonly IReadOnlyList<MapProvider> BuiltIn =
	[
		new(DefaultId, CoreStrings.Map_EsriName, MapTileFetcher.SatelliteUrlTemplate, MapTileFetcher.SatelliteAttribution),
		new("osm", "OpenStreetMap", MapTileFetcher.OpenStreetMapUrlTemplate, MapTileFetcher.OpenStreetMapAttribution),
		new("opentopomap", "OpenTopoMap", "https://a.tile.opentopomap.org/{z}/{x}/{y}.png", "© OpenStreetMap contributors, SRTM | © OpenTopoMap (CC-BY-SA)"),
		new("carto-positron", CoreStrings.Map_CartoPositronName, "https://basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png?key={api_key}", "© OpenStreetMap, © CARTO", CartoKeyGroup),
		new("carto-dark", CoreStrings.Map_CartoDarkName, "https://basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png?key={api_key}", "© OpenStreetMap, © CARTO", CartoKeyGroup),
		new("carto-voyager", "CARTO Voyager", "https://basemaps.cartocdn.com/rastertiles/voyager/{z}/{x}/{y}.png?key={api_key}", "© OpenStreetMap, © CARTO", CartoKeyGroup)
	];

	/// <summary>The built-in provider with this id; the default for null or an id this version doesn't know. Not the custom one - see MapSources.</summary>
	public static MapProvider Get(string? id)
	{
		return BuiltIn.FirstOrDefault(p => p.Id == id) ?? BuiltIn[0];
	}
}

/// <summary>
///     What turns a provider id into a tile URL and a credit line: the built-in list plus the settings shared by every
///     consumer - the API keys (one per KeyGroup, so a key typed for a widget is the same one the route overview uses) and
///     the custom provider's URL and credit. Built from OverlaySettings (From) and handed to the renderer.
/// </summary>
public sealed record MapSources(
	IReadOnlyDictionary<string, string>? ApiKeys = null,
	string? CustomUrlTemplate = null,
	string? CustomAttribution = null,
	bool ShowAttribution = true)
{
	public const string CustomKeyGroup = "custom";

	public static MapSources From(OverlaySettings settings)
	{
		return new MapSources(settings.MapApiKeys, settings.CustomMapUrlTemplate, settings.CustomMapAttribution, settings.MapShowAttribution);
	}

	/// <summary>The key group a provider's URL takes a key from, or null when it needs none.</summary>
	public string? KeyGroupOf(string? providerId)
	{
		return providerId == MapProviders.CustomId
			? CustomUrlTemplate?.Contains("{api_key}") == true ? CustomKeyGroup : null
			: MapProviders.Get(providerId).KeyGroup;
	}

	public string? ApiKey(string? group)
	{
		return group is not null && ApiKeys?.TryGetValue(group, out string? key) == true && !string.IsNullOrWhiteSpace(key) ? key.Trim() : null;
	}

	/// <summary>The tile URL with "{api_key}" filled in. Baking the key into the string means a key edit alone changes it, so the renderer's "already fetched?" comparisons catch it.</summary>
	public string UrlTemplate(string? providerId)
	{
		string template = providerId == MapProviders.CustomId
			? string.IsNullOrWhiteSpace(CustomUrlTemplate) ? MapTileFetcher.OpenStreetMapUrlTemplate : CustomUrlTemplate.Trim()
			: MapProviders.Get(providerId).UrlTemplate;
		return template.Replace("{api_key}", Uri.EscapeDataString(ApiKey(KeyGroupOf(providerId)) ?? ""));
	}

	/// <summary>The credit line the provider requires, or null when there is none to show or the user turned it off (ShowAttribution).</summary>
	public string? Attribution(string? providerId)
	{
		if (!ShowAttribution) return null;

		string? text = providerId == MapProviders.CustomId ? CustomAttribution : MapProviders.Get(providerId).Attribution;
		return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
	}
}
