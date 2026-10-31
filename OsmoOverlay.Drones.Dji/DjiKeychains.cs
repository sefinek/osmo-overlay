using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Drones.Dji;

/// <summary>
///     The keychains of an encrypted flight log, from DJI's flight record API (needs the SDK key of an Open API app from
///     developer.dji.com) or from the copy kept after the first time: the keys belong to the log, so a log is asked for once.
///     What is sent is the log's key blocks (not its positions); the SDK key itself is never stored here.
/// </summary>
public static class DjiKeychains
{
	private const string Endpoint = "https://dev.dji.com/openapi/v1/flight-records/keychains";

	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

	private static readonly string CacheFolder = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "dji-keychains");

	/// <summary>Where the SDK key comes from: the settings' (Settings > DJI drones), else OSMOOVERLAY_DJI_API_KEY. Tests replace it.</summary>
	public static Func<string?> ApiKey { get; set; } = () =>
		OverlaySettingsStore.Load().DjiApiKey is { Length: > 0 } key ? key : Environment.GetEnvironmentVariable("OSMOOVERLAY_DJI_API_KEY");

	/// <summary>Keychains of the log - cached ones first, else from DJI. Throws InvalidDataException with a message for the user.</summary>
	public static IReadOnlyList<Keychain> Obtain(FlightLog log)
	{
		KeychainsRequest request = log.BuildKeychainsRequest();
		string body = RequestJson(request).ToJsonString();
		string cache = Path.Combine(CacheFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))) + ".json");

		if (TryReadCache(cache) is { } cached) return cached;

		string apiKey = ApiKey()?.Trim() ?? "";
		if (apiKey.Length == 0) throw new InvalidDataException(CoreStrings.DjiLog_NeedsApiKey);

		JsonNode data = Fetch(apiKey, body);
		IReadOnlyList<Keychain> keychains = ParseResponse(data);
		try
		{
			Directory.CreateDirectory(CacheFolder);
			AtomicFile.WriteAllText(cache, data.ToJsonString());
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLogger.Warn(ex, "The flight log's keys could not be kept for next time.");
		}

		return keychains;
	}

	internal static JsonObject RequestJson(KeychainsRequest request)
	{
		var sessions = new JsonArray();
		foreach (List<(int FeaturePoint, byte[] Data)> session in request.Keychains)
		{
			var blocks = new JsonArray();
			foreach ((int featurePoint, byte[] data) in session)
			{
				blocks.Add(new JsonObject
				{
					["featurePoint"] = FlightLogFeaturePoints.Name(featurePoint),
					["aesCiphertext"] = Convert.ToBase64String(data)
				});
			}

			sessions.Add(blocks);
		}

		return new JsonObject { ["version"] = (int)request.Version, ["department"] = (int)request.Department, ["keychainsArray"] = sessions };
	}

	private static JsonNode Fetch(string apiKey, string body)
	{
		using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
		message.Headers.Add("Api-Key", apiKey);
		try
		{
			using HttpResponseMessage response = Http.Send(message);
			if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) throw new InvalidDataException(CoreStrings.DjiLog_KeyRejected);
			if (!response.IsSuccessStatusCode)
				throw new InvalidDataException(string.Format(CoreStrings.DjiLog_KeychainFailed, $"HTTP {(int)response.StatusCode}"));

			using Stream stream = response.Content.ReadAsStream();
			JsonNode? root = JsonNode.Parse(stream);
			int code = (int?)root?["result"]?["code"] ?? -1;
			if (code != 0)
				throw new InvalidDataException(string.Format(CoreStrings.DjiLog_KeychainFailed, (string?)root?["result"]?["msg"] ?? $"code {code}"));
			return root?["data"] ?? throw new InvalidDataException(string.Format(CoreStrings.DjiLog_KeychainFailed, "no data"));
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
		{
			throw new InvalidDataException(string.Format(CoreStrings.DjiLog_KeychainFailed, ex.Message), ex);
		}
	}

	private static IReadOnlyList<Keychain> ParseResponse(JsonNode data)
	{
		try
		{
			return Parse(data);
		}
		catch (Exception ex) when (ex is InvalidOperationException or FormatException)
		{
			throw new InvalidDataException(string.Format(CoreStrings.DjiLog_KeychainFailed, ex.Message), ex);
		}
	}

	private static IReadOnlyList<Keychain>? TryReadCache(string path)
	{
		try
		{
			return File.Exists(path) ? Parse(JsonNode.Parse(File.ReadAllText(path))!) : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or FormatException)
		{
			return null;
		}
	}

	/// <summary>The response's `data`: for each session a list of {featurePoint, aesKey, aesIv} with the key and IV in base64.</summary>
	internal static IReadOnlyList<Keychain> Parse(JsonNode data)
	{
		List<Keychain> keychains = [];
		foreach (JsonNode? session in data.AsArray())
		{
			var keys = new Dictionary<int, FeatureKey>();
			foreach (JsonNode? entry in session?.AsArray() ?? [])
			{
				int featurePoint = FlightLogFeaturePoints.FromName((string?)entry?["featurePoint"] ?? "");
				if (featurePoint == 0 || (string?)entry?["aesKey"] is not { } key || (string?)entry["aesIv"] is not { } iv) continue;
				keys[featurePoint] = new FeatureKey(Convert.FromBase64String(key), Convert.FromBase64String(iv));
			}

			keychains.Add(new Keychain(keys));
		}

		return keychains.Count > 0 ? keychains : throw new InvalidDataException(string.Format(CoreStrings.DjiLog_KeychainFailed, "empty"));
	}
}
