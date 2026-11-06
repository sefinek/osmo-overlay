using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Telemavix.Core.Localization;
using Telemavix.Core.Logging;
using Telemavix.Core.Updates;

namespace Telemavix.Core.Mapping;

public enum MapKeyVerdict
{
	Accepted,
	Rejected,
	Unverified
}

/// <summary>
///     Asks Esri whether a key is one. Its image service (ibasemaps-api) hands tiles to any non-empty token, so the check goes to
///     the basemap styles service the Navigation map is served from, which answers 401 (code 498/499) to a token it doesn't know. A
///     rejected key counts as no key (MapSources.ApiKey) - the public server is used, with the terms that come with it - for as long as
///     the process lives; an answer that says nothing (offline, a server's trouble) is not remembered and asked again.
/// </summary>
public static class MapKeyCheck
{
	private const string ProbeUrl = "https://static-map-tiles-api.arcgis.com/arcgis/rest/services/static-basemap-tiles-service/v1/arcgis/navigation/static/tile/0/0/0?token=";

	private static readonly HttpClient Http = CreateHttpClient();
	private static readonly ConcurrentDictionary<string, MapKeyVerdict> Verdicts = new(StringComparer.Ordinal);

	private static HttpClient CreateHttpClient()
	{
		var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd($"Telemavix/{AppUpdates.CurrentVersion} (+{AppUpdates.RepositoryUrl})");
		return client;
	}

	/// <summary>True only for a key Esri has said it doesn't know - an unchecked one is taken as good.</summary>
	public static bool IsRejected(string? group, string? key)
	{
		return group == MapProviders.EsriKeyGroup && !string.IsNullOrWhiteSpace(key) && Verdicts.TryGetValue(key.Trim(), out MapKeyVerdict verdict) && verdict == MapKeyVerdict.Rejected;
	}

	/// <summary>The stored Esri key is asked about once per run; nothing to do without one.</summary>
	public static async Task EnsureAsync(IReadOnlyDictionary<string, string>? keys, CancellationToken ct = default)
	{
		if (keys is null || !keys.TryGetValue(MapProviders.EsriKeyGroup, out string? key) || string.IsNullOrWhiteSpace(key)) return;
		await CheckEsriAsync(key, ct);
	}

	public static async Task<MapKeyVerdict> CheckEsriAsync(string key, CancellationToken ct = default)
	{
		key = key.Trim();
		if (key.Length == 0) return MapKeyVerdict.Unverified;
		if (Verdicts.TryGetValue(key, out MapKeyVerdict known)) return known;

		MapKeyVerdict verdict = await CheckAsync(Http, key, ct);
		if (verdict == MapKeyVerdict.Unverified) return verdict;

		Verdicts[key] = verdict;
		if (verdict == MapKeyVerdict.Rejected) AppLogger.Warn(CoreStrings.Map_EsriKeyRejected);
		return verdict;
	}

	internal static async Task<MapKeyVerdict> CheckAsync(HttpClient http, string key, CancellationToken ct)
	{
		try
		{
			using HttpResponseMessage response = await http.GetAsync(ProbeUrl + Uri.EscapeDataString(key), ct);
			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return MapKeyVerdict.Rejected;
			if (!response.IsSuccessStatusCode) return MapKeyVerdict.Unverified;

			// Some ArcGIS services answer 200 with the error in a JSON body.
			if (response.Content.Headers.ContentType?.MediaType == "application/json")
				return IsTokenError(await response.Content.ReadAsStringAsync(ct)) ? MapKeyVerdict.Rejected : MapKeyVerdict.Unverified;

			return MapKeyVerdict.Accepted;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
		{
			return MapKeyVerdict.Unverified;
		}
	}

	private static bool IsTokenError(string json)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			return document.RootElement.TryGetProperty("error", out JsonElement error) && error.TryGetProperty("code", out JsonElement code) &&
			       code.TryGetInt32(out int number) && number is 498 or 499 or 403;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	internal static void Remember(string key, MapKeyVerdict verdict)
	{
		Verdicts[key.Trim()] = verdict;
	}

	internal static void Forget(string key)
	{
		Verdicts.TryRemove(key.Trim(), out _);
	}
}
