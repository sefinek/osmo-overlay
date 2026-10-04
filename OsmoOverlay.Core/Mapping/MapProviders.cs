using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Core.Mapping;

/// <summary>What a provider's own terms say about a use: allowed, allowed only on a paid plan, or not said at all.</summary>
public enum MapUse
{
	Allowed,
	PaidPlanOnly,
	NotStated,
	NotAllowed,
	// Allowed only within limits the provider's note spells out (MapTiler: channels of up to 100,000 subscribers).
	Conditional
}

/// <summary>How long a map's credit has to be on screen.</summary>
public enum MapCreditRule
{
	/// <summary>The whole time the map is - what most terms ask for ("visible wherever the map is displayed").</summary>
	WhileMapVisible,

	/// <summary>Long enough to be read; the credit widget's own timing decides (OverlayRenderer.MapCreditAt).</summary>
	Briefly
}

/// <summary>
///     A tile server's terms as the app follows them - shown in the map picker (MapSourcePicker) and acted on: the credit's
///     rule by the renderer, CacheDays by MapTileFetcher. Checked against each provider's own terms on 2026-10-04 (the
///     quotes are next to each provider in MapProviders.BuiltIn). Region null = the whole world.
/// </summary>
public sealed record MapTerms(
	MapUse Commercial,
	MapUse Video,
	MapCreditRule Credit,
	string TermsUrl,
	// Null: for as long as wanted; 0: never stored on disk.
	int? CacheDays = null,
	MapRegion? Region = null,
	string? Note = null);

/// <summary>A provider covering one area only - a latitude/longitude box around it.</summary>
public sealed record MapRegion(string Name, double South, double West, double North, double East)
{
	public bool Contains(MapBounds bounds)
	{
		return bounds.South >= South && bounds.North <= North && bounds.West >= West && bounds.East <= East;
	}
}

/// <summary>Where a route runs, in degrees.</summary>
public readonly record struct MapBounds(double South, double West, double North, double East)
{
	/// <summary>Around the frames with a position (no fix yet is (0,0)) - null without any.</summary>
	public static MapBounds? Of(IEnumerable<TelemetryFrame> frames)
	{
		List<TelemetryFrame> fixes = [.. frames.Where(f => f.Latitude != 0 || f.Longitude != 0)];
		if (fixes.Count == 0) return null;

		return new MapBounds(fixes.Min(f => f.Latitude), fixes.Min(f => f.Longitude), fixes.Max(f => f.Latitude), fixes.Max(f => f.Longitude));
	}
}

/// <summary>
///     One tile server. Providers that share an account share its API key (KeyGroup: every CARTO style takes the same
///     key); null means the URL has no "{api_key}" placeholder.
/// </summary>
public sealed record MapProvider(string Id, string Name, string UrlTemplate, string Attribution, MapTerms Terms, string? KeyGroup = null)
{
	public bool NeedsApiKey => KeyGroup is not null;
}

/// <summary>
///     The one list of tile servers every map-based widget (the Map widget, the route overview start card) picks from.
///     "satellite" (the default) isn't a server of its own: it's Esri's imagery, keyed when its key is set (MapSources.Resolve). "custom" is a hand-entered URL kept once in OverlaySettings (CustomMapUrlTemplate) - see
///     MapSources. Sharpest imagery first, then maps, and last what its terms rule out for a film. Left out on purpose: tile.openstreetmap.org (its policy forbids
///     fetching tiles the user isn't viewing - a render's mosaic is that) and Stadia Maps (its tiles in "film, television
///     or other medium" need a commercial licence).
/// </summary>
public static class MapProviders
{
	public const string AutoSatelliteId = "satellite";
	public const string DefaultId = AutoSatelliteId;
	public const string CustomId = "custom";
	public const string GugikId = "gugik";
	public const string EoxId = "eox-2016";
	public const string EsriImageryId = "esri-imagery";
	public const string EsriPublicId = "esri-public";
	public const string StreetsAutoId = "streets";
	public const string EsriStreetsId = "esri-streets";
	public const string EsriStreetsPublicId = "esri-streets-public";
	public const string MapTilerStreetsId = "maptiler-streets";
	public const string MapTilerSatelliteId = "maptiler-satellite";
	public const string CartoKeyGroup = "carto";
	public const string MapTilerKeyGroup = "maptiler";
	public const string ThunderforestKeyGroup = "thunderforest";
	public const string EsriKeyGroup = "esri";

	/// <summary>Where a key of each group is made - the picker links it next to the key field.</summary>
	public static readonly IReadOnlyDictionary<string, string> KeyPages = new Dictionary<string, string>
	{
		[EsriKeyGroup] = "https://location.arcgis.com/",
		[MapTilerKeyGroup] = "https://cloud.maptiler.com/account/keys/",
		[ThunderforestKeyGroup] = "https://manage.thunderforest.com/",
		[CartoKeyGroup] = "https://carto.com/basemaps/apikey/"
	};

	public static readonly MapRegion Poland = new(CoreStrings.Map_RegionPoland, 49.0, 14.07, 54.91, 24.16);

	// "Ortofotomapa jest dostępna bezpłatnie do pobrania i możliwa do dowolnego wykorzystania" - no rule for the credit's
	// duration; the service metadata names the source.
	private static readonly MapTerms GugikTerms = new(MapUse.Allowed, MapUse.Allowed, MapCreditRule.Briefly,
		"https://www.geoportal.gov.pl/pl/dane/ortofotomapa-orto/", Region: Poland);

	// 2016 is CC BY 4.0 ("you may stitch multiple requests to fit your needs"); the credit "must be clearly visible wherever
	// the imagery is displayed". 10 m a pixel: a blur at a close-up map's zoom.
	private static readonly MapTerms EoxTerms = new(MapUse.Allowed, MapUse.Allowed, MapCreditRule.WhileMapVisible,
		"https://cloudless.eox.at/license-non-commercial", Note: CoreStrings.Map_NoteLowDetail);

	// "Die Lizenz CC-BY-SA der Online-Karte erlaubt deren kommerzielle Nutzung"; the licence text is to be shown "stets",
	// "deutlich sichtbar"; the server is for apps as long as mass downloads don't overload it.
	private static readonly MapTerms OpenTopoMapTerms = new(MapUse.Allowed, MapUse.NotStated, MapCreditRule.WhileMapVisible,
		"https://opentopomap.org/about", Note: CoreStrings.Map_NoteSharedServer);

	// Videos "for usage in internet channels (YouTube, Vimeo, etc.) with a maximum 100,000 subscribers"; the free plan "is
	// limited to non-commercial use"; the credit "is required to be shown on screen while the map is displayed"; only a
	// "temporary personal cache".
	private static readonly MapTerms MapTilerTerms = new(MapUse.PaidPlanOnly, MapUse.Conditional, MapCreditRule.WhileMapVisible,
		"https://www.maptiler.com/terms/cloud/", 7, Note: CoreStrings.Map_NoteMapTilerVideo);

	// "Commercial use is permitted and encouraged"; map images are CC-BY-SA 4.0; tiles "may be cached in-browser and
	// on-device"; nothing about video.
	private static readonly MapTerms ThunderforestTerms = new(MapUse.Allowed, MapUse.NotStated, MapCreditRule.WhileMapVisible,
		"https://www.thunderforest.com/terms/");

	// Commercial use within the free tier's limit, each customer with their own key; the credit "visible on the map itself",
	// not hidden or faded; a client-side cache of at most 30 days.
	private static readonly MapTerms CartoTerms = new(MapUse.Allowed, MapUse.NotStated, MapCreditRule.WhileMapVisible,
		"https://carto.com/legal/basemap-terms/", 30, Note: CoreStrings.Map_NoteCarto);

	// ArcGIS Location Platform: commercial use on the free tier (2M basemap tiles a month); a film with Esri basemaps
	// is fine for an Esri customer who credits Esri and its imagery's sources (arcgis.esri.de, "Dos and Don'ts"); Data may
	// not be scraped, downloaded or stored (Master Agreement), so nothing goes to the disk cache.
	private static readonly MapTerms EsriTerms = new(MapUse.Allowed, MapUse.Allowed, MapCreditRule.WhileMapVisible,
		"https://www.esri.com/en-us/legal/terms/full-master-agreement", 0, Note: CoreStrings.Map_NoteEsri);

	// The same imagery without a key: Esri's public service is for Esri's own products and ArcGIS accounts - in another
	// app, let alone a published film, it's against its terms. The default's pick without a key because the user chose
	// it over GUGiK/EOX; its terms are shown as they are.
	private static readonly MapTerms EsriPublicTerms = new(MapUse.NotAllowed, MapUse.NotAllowed, MapCreditRule.WhileMapVisible,
		"https://www.esri.com/en-us/legal/terms/full-master-agreement", 0, Note: CoreStrings.Map_NoteEsriPublic);

	private static readonly MapTerms EsriStreetsPublicTerms = EsriPublicTerms with { Note = CoreStrings.Map_NoteEsriStreetsPublic };

	private const string OsmCredit = "© OpenStreetMap contributors";

	public static readonly IReadOnlyList<MapProvider> BuiltIn =
	[
		new(EsriImageryId, "Esri World Imagery", "https://ibasemaps-api.arcgis.com/arcgis/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}?token={api_key}",
			"Powered by Esri | Esri, Maxar, Earthstar Geographics, and the GIS User Community", EsriTerms, EsriKeyGroup),
		new(MapTilerSatelliteId, "MapTiler Satellite", "https://api.maptiler.com/maps/satellite/256/{z}/{x}/{y}.jpg?key={api_key}",
			$"© MapTiler {OsmCredit}", MapTilerTerms, MapTilerKeyGroup),
		new("maptiler-hybrid", "MapTiler Hybrid", "https://api.maptiler.com/maps/hybrid/256/{z}/{x}/{y}.jpg?key={api_key}",
			$"© MapTiler {OsmCredit}", MapTilerTerms, MapTilerKeyGroup),
		new(GugikId, CoreStrings.Map_GugikName,
			"https://mapy.geoportal.gov.pl/wss/service/PZGIK/ORTO/WMTS/StandardResolution?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
			"&LAYER=ORTOFOTOMAPA&STYLE=default&FORMAT=image/jpeg&tileMatrixSet=EPSG:3857&tileMatrix=EPSG:3857:{z}&tileRow={y}&tileCol={x}",
			"Orthophoto © Główny Urząd Geodezji i Kartografii", GugikTerms),
		new(EoxId, CoreStrings.Map_EoxName, "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless_3857/default/g/{z}/{y}/{x}.jpg",
			"EOxCloudless https://cloudless.eox.at by EOX IT Services GmbH (Contains modified Copernicus Sentinel data 2016 & 2017)", EoxTerms),
		new(EsriStreetsId, CoreStrings.Map_EsriStreetsName,
			"https://static-map-tiles-api.arcgis.com/arcgis/rest/services/static-basemap-tiles-service/v1/arcgis/navigation/static/tile/{hz}/{hy}/{hx}?token={api_key}",
			"Powered by Esri | Esri, TomTom, Garmin, FAO, NOAA, USGS, © OpenStreetMap contributors, and the GIS User Community", EsriTerms, EsriKeyGroup),
		new(MapTilerStreetsId, "MapTiler Streets", "https://api.maptiler.com/maps/streets-v2/256/{z}/{x}/{y}.png?key={api_key}",
			$"© MapTiler {OsmCredit}", MapTilerTerms, MapTilerKeyGroup),
		new("maptiler-topo", "MapTiler Topo", "https://api.maptiler.com/maps/topo-v2/256/{z}/{x}/{y}.png?key={api_key}",
			$"© MapTiler {OsmCredit}", MapTilerTerms, MapTilerKeyGroup),
		new("maptiler-outdoor", "MapTiler Outdoor", "https://api.maptiler.com/maps/outdoor-v2/256/{z}/{x}/{y}.png?key={api_key}",
			$"© MapTiler {OsmCredit}", MapTilerTerms, MapTilerKeyGroup),
		new("opentopomap", "OpenTopoMap", "https://a.tile.opentopomap.org/{z}/{x}/{y}.png",
			$"Map data: {OsmCredit}, SRTM | Map style: © OpenTopoMap (CC-BY-SA)", OpenTopoMapTerms),
		new("thunderforest-cycle", "Thunderforest OpenCycleMap", "https://tile.thunderforest.com/cycle/{z}/{x}/{y}.png?apikey={api_key}",
			"Maps © www.thunderforest.com, Data © www.osm.org/copyright", ThunderforestTerms, ThunderforestKeyGroup),
		new("thunderforest-outdoors", "Thunderforest Outdoors", "https://tile.thunderforest.com/outdoors/{z}/{x}/{y}.png?apikey={api_key}",
			"Maps © www.thunderforest.com, Data © www.osm.org/copyright", ThunderforestTerms, ThunderforestKeyGroup),
		new("thunderforest-landscape", "Thunderforest Landscape", "https://tile.thunderforest.com/landscape/{z}/{x}/{y}.png?apikey={api_key}",
			"Maps © www.thunderforest.com, Data © www.osm.org/copyright", ThunderforestTerms, ThunderforestKeyGroup),
		new("thunderforest-atlas", "Thunderforest Atlas", "https://tile.thunderforest.com/atlas/{z}/{x}/{y}.png?apikey={api_key}",
			"Maps © www.thunderforest.com, Data © www.osm.org/copyright", ThunderforestTerms, ThunderforestKeyGroup),
		new("carto-positron", CoreStrings.Map_CartoPositronName, "https://basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png?key={api_key}",
			$"{OsmCredit}, © CARTO", CartoTerms, CartoKeyGroup),
		new("carto-dark", CoreStrings.Map_CartoDarkName, "https://basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png?key={api_key}",
			$"{OsmCredit}, © CARTO", CartoTerms, CartoKeyGroup),
		new("carto-voyager", "CARTO Voyager", "https://basemaps.cartocdn.com/rastertiles/voyager/{z}/{x}/{y}.png?key={api_key}",
			$"{OsmCredit}, © CARTO", CartoTerms, CartoKeyGroup),
		new(EsriPublicId, CoreStrings.Map_EsriPublicName,
			"https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
			"Esri, Maxar, Earthstar Geographics, and the GIS User Community", EsriPublicTerms),
		new(EsriStreetsPublicId, CoreStrings.Map_EsriStreetsPublicName,
			"https://server.arcgisonline.com/ArcGIS/rest/services/World_Street_Map/MapServer/tile/{z}/{y}/{x}",
			"Esri, HERE, Garmin, USGS, © OpenStreetMap contributors, and the GIS User Community", EsriStreetsPublicTerms)
	];

	/// <summary>
	///     The built-in provider with this id - the default's pick for a route nobody knows yet (EOX, the one covering the
	///     world) for null, the auto id or an id this version doesn't know (a removed one: "esri", "osm"). Not the custom one.
	/// </summary>
	public static MapProvider Get(string? id)
	{
		return BuiltIn.FirstOrDefault(p => p.Id == id) ?? BuiltIn.First(p => p.Id == EoxId);
	}
}

/// <summary>
///     What turns a provider id into a tile URL and a credit line: the built-in list plus the settings shared by every
///     consumer - the API keys (one per KeyGroup, so a key typed for a widget is the same one the route overview uses) and
///     the custom provider's URL, credit and its credit's rule. Built from OverlaySettings (From) and handed to the
///     renderer, which adds the route's bounds (RouteBounds) - no pick reads them since the default stopped choosing by region.
/// </summary>
public sealed record MapSources(
	IReadOnlyDictionary<string, string>? ApiKeys = null,
	string? CustomUrlTemplate = null,
	string? CustomAttribution = null,
	bool CustomCreditBriefly = false,
	MapBounds? RouteBounds = null,
	string? DefaultProviderId = null,
	bool ShowEsriLabels = true,
	bool ShowRouteIntroEsriLabels = false)
{
	public const string CustomKeyGroup = "custom";

	public static MapSources From(OverlaySettings settings)
	{
		return new MapSources(settings.MapApiKeys, settings.CustomMapUrlTemplate, settings.CustomMapAttribution, settings.CustomMapCreditBriefly,
			DefaultProviderId: settings.DefaultMapProvider, ShowEsriLabels: settings.ShowEsriMapLabels,
			ShowRouteIntroEsriLabels: settings.RouteIntroShowEsriMapLabels);
	}

	/// <summary>
	///     The provider a map really uses. No choice (null) is the default style (DefaultProviderId, satellite unless the
	///     first-run window said streets). The two "best available" picks are Esri's, one source: with its key the keyed
	///     service, else Esri's public one, as the user chose over GUGiK/EOX. Any other id as it is.
	/// </summary>
	public string Resolve(string? providerId)
	{
		providerId ??= DefaultProviderId ?? MapProviders.AutoSatelliteId;
		if (providerId == MapProviders.CustomId) return providerId;
		if (providerId != MapProviders.AutoSatelliteId && providerId != MapProviders.StreetsAutoId && MapProviders.BuiltIn.Any(p => p.Id == providerId))
			return providerId;

		bool streets = providerId == MapProviders.StreetsAutoId;
		if (ApiKey(MapProviders.EsriKeyGroup) is not null) return streets ? MapProviders.EsriStreetsId : MapProviders.EsriImageryId;
		return streets ? MapProviders.EsriStreetsPublicId : MapProviders.EsriPublicId;
	}

	/// <summary>The key group a provider's URL takes a key from, or null when it needs none.</summary>
	public string? KeyGroupOf(string? providerId)
	{
		string id = Resolve(providerId);
		return id == MapProviders.CustomId
			? CustomUrlTemplate?.Contains("{api_key}") == true ? CustomKeyGroup : null
			: MapProviders.Get(id).KeyGroup;
	}

	public string? ApiKey(string? group)
	{
		return group is not null && ApiKeys?.TryGetValue(group, out string? key) == true && !string.IsNullOrWhiteSpace(key) ? key.Trim() : null;
	}

	/// <summary>The provider's URL takes a key and none is set - its server would refuse every tile, so nothing is fetched.</summary>
	public bool MissesApiKey(string? providerId)
	{
		return KeyGroupOf(providerId) is { } group && ApiKey(group) is null;
	}

	/// <summary>The provider's name as the picker shows it - the custom server's is its URL's host.</summary>
	public string NameOf(string? providerId)
	{
		string id = Resolve(providerId);
		if (id != MapProviders.CustomId) return MapProviders.Get(id).Name;
		return Uri.TryCreate(CustomUrlTemplate?.Trim(), UriKind.Absolute, out Uri? uri) ? uri.Host : MapProviders.CustomId;
	}

	/// <summary>
	///     The tile URL with "{api_key}" filled in. Baking the key into the string means a key edit alone changes it, so the
	///     renderer's "already fetched?" comparisons catch it. An empty custom URL falls back to the default provider's.
	/// </summary>
	public string UrlTemplate(string? providerId)
	{
		string id = Resolve(providerId);
		string template = id == MapProviders.CustomId
			? string.IsNullOrWhiteSpace(CustomUrlTemplate) ? MapProviders.Get(Resolve(null)).UrlTemplate : CustomUrlTemplate.Trim()
			: MapProviders.Get(id).UrlTemplate;
		return template.Replace("{api_key}", Uri.EscapeDataString(ApiKey(KeyGroupOf(id)) ?? ""));
	}

	public bool SupportsLabels(string? providerId)
	{
		return Resolve(providerId) is MapProviders.EsriImageryId or MapProviders.EsriPublicId;
	}

	public string? LabelsUrlTemplate(string? providerId, bool routeIntro = false)
	{
		if (!(routeIntro ? ShowRouteIntroEsriLabels : ShowEsriLabels) || !SupportsLabels(providerId)) return null;
		if (Resolve(providerId) == MapProviders.EsriPublicId)
			return "https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}";

		if (ApiKey(MapProviders.EsriKeyGroup) is not { } key) return null;
		return "https://static-map-tiles-api.arcgis.com/arcgis/rest/services/static-basemap-tiles-service/v1/arcgis/imagery/labels/static/tile/{hz}/{hy}/{hx}?token=" + Uri.EscapeDataString(key);
	}

	/// <summary>The credit line the provider requires - null for a custom server given none.</summary>
	public string? Attribution(string? providerId, bool routeIntro = false)
	{
		string id = Resolve(providerId);
		if (LabelsUrlTemplate(providerId, routeIntro) is not null)
		{
			return id == MapProviders.EsriPublicId
				? "Esri, Maxar, Earthstar Geographics, HERE, Garmin, and the GIS User Community"
				: "Powered by Esri | Esri, Maxar, Earthstar Geographics, TomTom, Garmin, FAO, NOAA, USGS, © OpenStreetMap contributors, and the GIS User Community";
		}

		string? text = id == MapProviders.CustomId ? CustomAttribution : MapProviders.Get(id).Attribution;
		return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
	}

	/// <summary>How long the credit must stay: the provider's terms, or the user's own word for a custom server.</summary>
	public MapCreditRule CreditRule(string? providerId)
	{
		string id = Resolve(providerId);
		if (id != MapProviders.CustomId) return MapProviders.Get(id).Terms.Credit;
		return CustomCreditBriefly ? MapCreditRule.Briefly : MapCreditRule.WhileMapVisible;
	}

	/// <summary>How long a fetched tile may be kept - null for as long as wanted.</summary>
	public TimeSpan? CacheMaxAge(string? providerId)
	{
		string id = Resolve(providerId);
		return id != MapProviders.CustomId && MapProviders.Get(id).Terms.CacheDays is { } days ? TimeSpan.FromDays(days) : null;
	}
}
