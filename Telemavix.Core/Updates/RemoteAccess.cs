using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NuGet.Versioning;
using Telemavix.Core.Logging;

namespace Telemavix.Core.Updates;

/// <param name="Blocked">This version is below the lowest one the server still allows.</param>
/// <param name="MinVersion">The lowest allowed version; null when the server sets no limit.</param>
/// <param name="Url">Where to point the user (a site to get a newer version from); null when there is none.</param>
/// <param name="IssuedAt">When the server signed the answer; null for no answer at all.</param>
public sealed record AccessDecision(bool Blocked, NuGetVersion? MinVersion, string? Url, DateTimeOffset? IssuedAt = null)
{
	public static AccessDecision Allowed { get; } = new(false, null, null);
}

/// <summary>
///     The server's switch for old versions (api.sefinek.net, TELEMAVIX_MIN_VERSION): a version below the lowest allowed
///     one doesn't run. Off while the server sets no minimum. The answer is signed (ECDSA P-256) and checked against the
///     keys built in here (PublicKeys: the one in use and a spare, so a leaked signing key is replaced on the server alone),
///     an answer older than the one already kept is refused (a replay), so a copy that can't reach the server, or one that gets a made-up answer, is not blocked by it -
///     and a block already seen is kept in access.json, so going offline doesn't lift it. No answer and no kept block
///     mean the app runs: a failing network or server never stops anyone by itself.
/// </summary>
public static class RemoteAccess
{
	private const string Endpoint = "https://api.sefinek.net/api/v2/telemavix/access";

	// The public halves (SubjectPublicKeyInfo, base64) of the keys the API may sign with (TELEMAVIX_SIGNING_KEY there): the one in use,
	// then a spare whose private half is kept offline. Rotating means putting the spare's private key on the server.
	// Changing or removing a key breaks the check of every released copy that doesn't know the new one.
	internal static readonly string[] PublicKeys =
	[
		"MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEvdpj+aCOAQIg7j1K3m37YCywIW3HnbffLZctsB7gueSkXllRxrNWU2YR9VMosTEmvzBMyYz/7JPZr6QO3PlNZg==",
		"MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEKZhOCIHj7ySWnS4gWbshBi8PtytC6AByQbGh1GoFLC13TviK5YMtw2fMxfS1BtHs+77cnLUwpwJiZfyzHYz4wA=="
	];

	private static readonly HttpClient Http = CreateHttpClient();

	private static readonly string CachePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Telemavix", "access.json");

	private static HttpClient CreateHttpClient()
	{
		var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		client.DefaultRequestHeaders.UserAgent.ParseAdd($"Telemavix/{AppUpdates.CurrentVersionLabel} (+{AppUpdates.RepositoryUrl})");
		return client;
	}

	/// <summary>Asks the server; when it can't be reached or its answer doesn't check out, the last block seen (Cached) decides.</summary>
	public static async Task<AccessDecision> CheckAsync(CancellationToken ct)
	{
		try
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(TimeSpan.FromSeconds(8));

			string response = await Http.GetStringAsync(Endpoint, timeout.Token);
			DateTimeOffset now = DateTimeOffset.UtcNow;
			AccessDecision decision = Evaluate(response, now, true);
			if (IsReplay(decision, Cached(), now)) throw new InvalidDataException("Access response is older than the one already kept.");
			Save(response);
			if (decision.Blocked) AppLogger.Warn($"Version {AppUpdates.CurrentVersionLabel} is switched off by the server (minimum: {decision.MinVersion}).");
			return decision;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException
			                           or CryptographicException or FormatException)
		{
			if (ct.IsCancellationRequested) throw;
			AppLogger.Info($"Access check skipped: {ex.Message}");
			return Cached();
		}
	}

	/// <summary>
	///     An answer signed before the kept one is an old one served again. A kept answer dated well ahead of this clock proves
	///     nothing (the clock may be the one that is wrong), so it doesn't hold a newer answer back.
	/// </summary>
	internal static bool IsReplay(AccessDecision received, AccessDecision kept, DateTimeOffset now)
	{
		return received.IssuedAt is { } at && kept.IssuedAt is { } keptAt && keptAt <= now.AddHours(1) && at < keptAt;
	}

	/// <summary>The last signed answer kept on disk, no network; allowed when there is none or it doesn't check out.</summary>
	public static AccessDecision Cached()
	{
		try
		{
			return File.Exists(CachePath) ? Evaluate(File.ReadAllText(CachePath), DateTimeOffset.UtcNow, false) : AccessDecision.Allowed;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException
			                           or CryptographicException or FormatException)
		{
			return AccessDecision.Allowed;
		}
	}

	private static void Save(string response)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
			File.WriteAllText(CachePath, response);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			AppLogger.Warn(ex, "Could not keep the access answer.");
		}
	}

	private static AccessDecision Evaluate(string response, DateTimeOffset now, bool requireFresh)
	{
		List<ECDsa> keys = [];
		try
		{
			foreach (string publicKey in PublicKeys)
			{
				ECDsa key = ECDsa.Create();
				keys.Add(key);
				key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
			}

			return Evaluate(response, keys, AppUpdates.CurrentVersion, now, requireFresh);
		}
		finally
		{
			foreach (ECDsa key in keys) key.Dispose();
		}
	}

	internal static AccessDecision Evaluate(string response, ECDsa key, NuGetVersion current, DateTimeOffset now, bool requireFresh)
	{
		return Evaluate(response, [key], current, now, requireFresh);
	}

	/// <param name="requireFresh">The answer just came from the server, so one past its expiry is refused (a replayed old one).</param>
	/// <exception cref="InvalidDataException">The response is incomplete, its signature is wrong or it has expired.</exception>
	internal static AccessDecision Evaluate(string response, IReadOnlyList<ECDsa> keys, NuGetVersion current, DateTimeOffset now, bool requireFresh)
	{
		JsonNode json = JsonNode.Parse(response) ?? throw new InvalidDataException("Empty access response.");
		string payload = json["payload"]?.GetValue<string>() ?? throw new InvalidDataException("Access response has no payload.");
		string signature = json["signature"]?.GetValue<string>() ?? throw new InvalidDataException("Access response has no signature.");

		byte[] data = Encoding.UTF8.GetBytes(payload);
		byte[] signatureBytes = Convert.FromBase64String(signature);
		if (!keys.Any(key => key.VerifyData(data, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)))
			throw new InvalidDataException("Access response signature doesn't match.");

		JsonNode body = JsonNode.Parse(payload) ?? throw new InvalidDataException("Empty access payload.");
		if (requireFresh && body["expiresAt"]?.GetValue<DateTimeOffset>() is { } expires && now > expires)
			throw new InvalidDataException("Access response has expired.");

		DateTimeOffset? issuedAt = body["issuedAt"]?.GetValue<DateTimeOffset>();
		string? min = body["minVersion"]?.GetValue<string>();
		if (string.IsNullOrWhiteSpace(min)) return new AccessDecision(false, null, null, issuedAt);
		if (!NuGetVersion.TryParse(min, out NuGetVersion? minVersion)) throw new InvalidDataException($"Minimum version '{min}' isn't a version number.");

		string? url = body["url"]?.GetValue<string>();
		return new AccessDecision(AppUpdates.IsNewer(minVersion, current), minVersion,
			Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null, issuedAt);
	}
}
