using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Updates;

/// <param name="Sha256">Lowercase hex, from the asset's own digest or the release's SHA256SUMS file; null when neither has it.</param>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256);

/// <param name="Installer">The Windows installer for this machine's architecture, null when the release has none (or off Windows).</param>
public sealed record AppRelease(Version Version, string PageUrl, DateTimeOffset? PublishedAt, ReleaseAsset? Installer);

/// <summary>
///     The app's own updates, from the GitHub repository's latest release (prereleases and drafts excluded by the API).
///     On Windows an installed copy updates itself: the release's setup is downloaded, verified against its SHA-256 and
///     started with /UPDATE, which removes the previous version and starts the app again (OsmoOverlay.Build/Installer).
///     A copy that wasn't installed by that setup (an extracted zip) only gets pointed at the release page.
/// </summary>
public static class AppUpdates
{
	public const string RepositoryUrl = "https://github.com/sefinek/osmo-overlay";
	public const string ReleasesUrl = RepositoryUrl + "/releases";

	// Must match AppGuid in OsmoOverlay.Build/Installer/OsmoOverlay.iss.
	private const string InstallerGuid = "285212C3-78F8-4A92-AE19-52D33596D266";

	// The installer's AppMutex - the GUI holds it while running, so setup knows to wait for it to close.
	public const string MutexName = @"Global\OsmoOverlay-" + InstallerGuid;

	private const string LatestReleaseApi = "https://api.github.com/repos/sefinek/osmo-overlay/releases/latest";

	// Before Http: static initializers run in declaration order, and the User-Agent reads it.
	public static Version CurrentVersion { get; } = ThreeParts(typeof(AppUpdates).Assembly.GetName().Version ?? new Version(0, 0, 0));

	private static readonly HttpClient Http = CreateHttpClient();

	private static HttpClient CreateHttpClient()
	{
		var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		client.DefaultRequestHeaders.UserAgent.ParseAdd($"OsmoOverlay/{CurrentVersion} (+{RepositoryUrl})");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		return client;
	}

	/// <summary>Null when the repository has no release yet. Throws on network/API failures. Logs the check and its outcome.</summary>
	public static async Task<AppRelease?> GetLatestAsync(CancellationToken ct)
	{
		AppLogger.Notify($"Checking for a new version of OsmoOverlay (current: {CurrentVersion})...");
		AppRelease? release = await FetchLatestAsync(ct);

		AppLogger.Notify(release switch
		{
			null => "No OsmoOverlay release has been published yet",
			_ when IsNewer(release) => $"OsmoOverlay {release.Version} is available (you have {CurrentVersion})",
			_ => $"OsmoOverlay is up to date ({CurrentVersion})"
		});
		return release;
	}

	private static async Task<AppRelease?> FetchLatestAsync(CancellationToken ct)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(20));

		using HttpResponseMessage response = await Http.GetAsync(LatestReleaseApi, timeout.Token);
		if (response.StatusCode == HttpStatusCode.NotFound) return null;
		response.EnsureSuccessStatusCode();

		JsonNode json = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token))
		                ?? throw new InvalidDataException("Empty release response.");
		(AppRelease release, var sumsUrl) = ParseRelease(json, InstallerSuffix());

		// Assets uploaded before GitHub started publishing digests have none - the release's SHA256SUMS file does.
		if (release.Installer is { Sha256: null } installer && sumsUrl is not null)
			release = release with { Installer = installer with { Sha256 = FindChecksum(await Http.GetStringAsync(sumsUrl, timeout.Token), installer.Name) } };

		return release;
	}

	/// <param name="installerSuffix">The end of this machine's installer's file name (InstallerSuffix), null for none.</param>
	/// <returns>The release, and the SHA256SUMS file's URL when the installer has no digest of its own.</returns>
	internal static (AppRelease Release, string? SumsUrl) ParseRelease(JsonNode release, string? installerSuffix)
	{
		var tag = release["tag_name"]?.GetValue<string>() ?? throw new InvalidDataException("Release has no tag.");
		if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-', '+')[0], out Version? version))
			throw new InvalidDataException($"Release tag '{tag}' isn't a version number.");

		List<JsonNode> assets = [.. (release["assets"]?.AsArray() ?? []).OfType<JsonNode>()];
		ReleaseAsset? installer = null;
		string? sumsUrl = null;
		if (installerSuffix is not null && AssetEndingWith(assets, installerSuffix) is { } asset)
		{
			var name = asset["name"]!.GetValue<string>();
			var sha256 = asset["digest"]?.GetValue<string>() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
				? digest["sha256:".Length..].ToLowerInvariant()
				: null;
			if (sha256 is null) sumsUrl = AssetEndingWith(assets, "SHA256SUMS.txt")?["browser_download_url"]?.GetValue<string>();
			installer = new ReleaseAsset(name, asset["browser_download_url"]!.GetValue<string>(), asset["size"]?.GetValue<long>() ?? 0, sha256);
		}

		return (new AppRelease(ThreeParts(version), release["html_url"]?.GetValue<string>() ?? ReleasesUrl,
			release["published_at"]?.GetValue<DateTimeOffset>(), installer), sumsUrl);
	}

	/// <summary>The hash for `fileName` from a sha256sum-style file ("hash  name" per line); null when it isn't listed.</summary>
	internal static string? FindChecksum(string sums, string fileName)
	{
		return sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(line => line.Split(' ', 2, StringSplitOptions.TrimEntries))
			.FirstOrDefault(parts => parts.Length == 2 && parts[1].TrimStart('*') == fileName)?[0].ToLowerInvariant();
	}

	private static JsonNode? AssetEndingWith(List<JsonNode> assets, string suffix)
	{
		return assets.FirstOrDefault(a => a["name"]?.GetValue<string>().EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true);
	}

	public static bool IsNewer(AppRelease release)
	{
		return release.Version > CurrentVersion;
	}

	/// <summary>
	///     True only for a copy the setup installed, running from where it installed it - the update installs over that
	///     same folder. An extracted zip (or a dev build) would get a second, installed copy instead.
	/// </summary>
	public static bool CanUpdateInPlace(AppRelease release)
	{
		return release.Installer is not null && InstalledLocation() is { } location &&
		       string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)),
			       Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Downloads the release's installer to the temp folder and checks it against its SHA-256; returns its path.</summary>
	public static async Task<string> DownloadInstallerAsync(ReleaseAsset installer, IProgress<double>? progress, CancellationToken ct)
	{
		var path = Path.Combine(Path.GetTempPath(), installer.Name);
		var partialPath = path + ".partial";

		using (HttpResponseMessage response = await Http.GetAsync(installer.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
		{
			response.EnsureSuccessStatusCode();
			var total = response.Content.Headers.ContentLength ?? installer.Size;
			await using Stream source = await response.Content.ReadAsStreamAsync(ct);
			await using FileStream target = File.Create(partialPath);

			var buffer = new byte[81920];
			long received = 0;
			int read;
			while ((read = await source.ReadAsync(buffer, ct)) > 0)
			{
				await target.WriteAsync(buffer.AsMemory(0, read), ct);
				received += read;
				if (total > 0) progress?.Report((double)received / total);
			}
		}

		if (installer.Sha256 is null)
		{
			AppLogger.Warn($"{installer.Name} has no published SHA-256 - installing it unverified");
		}
		else
		{
			await using FileStream downloaded = File.OpenRead(partialPath);
			var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(downloaded, ct));
			if (actual != installer.Sha256)
			{
				downloaded.Close();
				File.Delete(partialPath);
				throw new InvalidDataException($"{installer.Name} doesn't match its published SHA-256 - the download is corrupt or was tampered with.");
			}
		}

		File.Move(partialPath, path, true);
		return path;
	}

	/// <summary>
	///     Starts the downloaded setup in update mode - the caller exits right after, and setup waits for it (AppMutex),
	///     replaces the installed copy and starts the new version. Its log goes next to app.log.
	/// </summary>
	public static void StartInstaller(string installerPath)
	{
		var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "logs");
		Directory.CreateDirectory(logDir);
		var logFile = Path.Combine(logDir, $"update-{DateTime.Now:yyyyMMdd-HHmmss}.log");

		// Deliberately not ProcessHelper: setup has its own visible progress window and may ask for elevation.
		var psi = new ProcessStartInfo(installerPath) { UseShellExecute = true };
		foreach (var argument in (string[])["/UPDATE", "/SILENT", "/NORESTART", $"/LOG={logFile}"])
			psi.ArgumentList.Add(argument);
		Process.Start(psi)?.Dispose();
		AppLogger.Info($"Started the update: {installerPath} (log: {logFile})");
	}

	private static string? InstallerSuffix()
	{
		if (!OperatingSystem.IsWindows()) return null;

		return RuntimeInformation.OSArchitecture switch
		{
			Architecture.X64 => "-win-x64-setup.exe",
			Architecture.Arm64 => "-win-arm64-setup.exe",
			_ => null
		};
	}

	private static string? InstalledLocation()
	{
		if (!OperatingSystem.IsWindows()) return null;

		const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + InstallerGuid + "}_is1";
		foreach (RegistryKey hive in (RegistryKey[])[Registry.CurrentUser, Registry.LocalMachine])
		{
			using RegistryKey? uninstall = hive.OpenSubKey(key);
			if (uninstall?.GetValue("InstallLocation") is string { Length: > 0 } location) return location;
		}

		return null;
	}

	private static Version ThreeParts(Version version)
	{
		return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
	}
}
