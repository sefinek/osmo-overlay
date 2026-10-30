using System.Collections.Concurrent;
using System.Net;

namespace OsmoOverlay.Core.Mapping;

internal sealed class MapTileHttpClient(HttpClient http, TimeProvider? timeProvider = null,
	Func<TimeSpan, CancellationToken, Task>? delay = null, Action<string, TimeSpan>? onCooldown = null, Action<string, HttpStatusCode>? onAccessDenied = null)
{
	private static readonly TimeSpan InitialCooldown = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

	// A server that answers 401/403 to this many requests in a row, with no tile in between, isn't going to serve the rest (a wrong or
	// unauthorized API key): they fail at once for a while instead of each one asking and being refused.
	private const int AccessDeniedLimit = 3;
	private static readonly TimeSpan AccessDeniedCooldown = TimeSpan.FromMinutes(1);
	private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
	private readonly ConcurrentDictionary<string, ServerState> _servers = new(StringComparer.OrdinalIgnoreCase);

	private sealed class ServerState
	{
		public readonly Lock Sync = new();
		public readonly SemaphoreSlim Recovery = new(1, 1);
		public DateTimeOffset ResumeAt;
		public int Failures;
		public bool Throttled;
		public long Version;
		public int Denied;
		public DateTimeOffset DeniedUntil;
		public HttpStatusCode DeniedStatus;
	}

	public async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
	{
		var uri = new Uri(url);
		ServerState server = _servers.GetOrAdd(uri.Authority, _ => new ServerState());
		ThrowIfDenied(server);
		await server.Recovery.WaitAsync(ct);
		bool holdingRecovery = true;
		try
		{
			bool recovering;
			long recoveryVersion = 0;
			lock (server.Sync) recovering = server.Throttled;
			if (recovering)
			{
				// One request tests recovery; the other tiles wait instead of all retrying together.
				await WaitForCooldownAsync(server, ct);
				lock (server.Sync) recoveryVersion = server.Version;
			}
			else
			{
				server.Recovery.Release();
				holdingRecovery = false;
			}

			using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
			if (response.StatusCode == HttpStatusCode.TooManyRequests ||
			    response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.RetryAfter is not null)
			{
				RegisterCooldown(server, uri.Authority, response);
			}
			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) RegisterDenied(server, uri.Authority, response.StatusCode);
			response.EnsureSuccessStatusCode();
			byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
			lock (server.Sync) server.Denied = 0;
			if (recovering)
			{
				lock (server.Sync)
				{
					if (server.Version == recoveryVersion)
					{
						server.Throttled = false;
						server.Failures = 0;
					}
				}
			}
			return bytes;
		}
		finally
		{
			if (holdingRecovery) server.Recovery.Release();
		}
	}

	private void ThrowIfDenied(ServerState server)
	{
		HttpStatusCode status;
		lock (server.Sync)
		{
			if (server.Denied < AccessDeniedLimit || _time.GetUtcNow() >= server.DeniedUntil) return;

			status = server.DeniedStatus;
		}

		throw new HttpRequestException($"The server refused the earlier requests (HTTP {(int)status}).", null, status);
	}

	private void RegisterDenied(ServerState server, string host, HttpStatusCode status)
	{
		DateTimeOffset now = _time.GetUtcNow();
		bool announce;
		lock (server.Sync)
		{
			// Said once when the refusals start, and again whenever a request let through after the pause is refused too.
			announce = server.Denied == 0 || server.Denied >= AccessDeniedLimit && now >= server.DeniedUntil;
			server.Denied = Math.Min(server.Denied + 1, AccessDeniedLimit);
			server.DeniedStatus = status;
			if (server.Denied >= AccessDeniedLimit) server.DeniedUntil = now + AccessDeniedCooldown;
		}

		if (announce) onAccessDenied?.Invoke(host, status);
	}

	private async Task WaitForCooldownAsync(ServerState server, CancellationToken ct)
	{
		while (true)
		{
			ct.ThrowIfCancellationRequested();
			TimeSpan remaining;
			lock (server.Sync) remaining = server.ResumeAt - _time.GetUtcNow();
			if (remaining <= TimeSpan.Zero) return;
			TimeSpan chunk = remaining > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : remaining;
			await (delay?.Invoke(chunk, ct) ?? Task.Delay(chunk, _time, ct));
		}
	}

	private void RegisterCooldown(ServerState server, string host, HttpResponseMessage response)
	{
		DateTimeOffset now = _time.GetUtcNow();
		TimeSpan retryAfter = response.Headers.RetryAfter switch
		{
			{ Delta: { } delta } => delta,
			{ Date: { } date } => date - now,
			_ => TimeSpan.Zero
		};
		bool announce;
		TimeSpan cooldown;
		lock (server.Sync)
		{
			announce = !server.Throttled || server.ResumeAt <= now;
			if (announce) server.Failures = Math.Min(server.Failures + 1, 5);
			cooldown = TimeSpan.FromSeconds(Math.Min(InitialCooldown.TotalSeconds * Math.Pow(2, server.Failures - 1), MaxBackoff.TotalSeconds));
			if (retryAfter > cooldown) cooldown = retryAfter;
			DateTimeOffset resumeAt = cooldown > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + cooldown;
			if (resumeAt > server.ResumeAt) server.ResumeAt = resumeAt;
			server.Throttled = true;
			server.Version++;
		}
		if (announce) onCooldown?.Invoke(host, cooldown);
	}
}
