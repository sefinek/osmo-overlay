using System.Text.Json.Nodes;
using OsmoOverlay.Core.Updates;

namespace OsmoOverlay.Tests.Updates;

/// <summary>ParseRelease on hand-made GitHub API responses shaped like a real "latest release".</summary>
[TestClass]
public sealed class AppUpdatesTests
{
	private const string Suffix = "-win-x64-setup.exe";

	private static JsonNode Release(string tag, string assets)
	{
		return JsonNode.Parse($$"""
		                        {
		                          "tag_name": "{{tag}}",
		                          "html_url": "https://github.com/sefinek/osmo-overlay/releases/tag/{{tag}}",
		                          "published_at": "2026-10-01T12:00:00Z",
		                          "assets": [{{assets}}]
		                        }
		                        """)!;
	}

	private static string Asset(string name, string? digest = null)
	{
		var digestField = digest is null ? "" : $"\"digest\": \"{digest}\",";
		return $$"""{ "name": "{{name}}", {{digestField}} "size": 1234, "browser_download_url": "https://example.test/{{name}}" }""";
	}

	[TestMethod]
	public void CurrentVersion_InitializesTheTypeAndItsHttpClient()
	{
		Version assembly = typeof(AppUpdates).Assembly.GetName().Version!;

		Assert.AreEqual(new Version(assembly.Major, assembly.Minor, assembly.Build), AppUpdates.CurrentVersion);
	}

	[TestMethod]
	public void ParseRelease_PicksThisArchitecturesInstaller_WithItsDigest()
	{
		JsonNode json = Release("v0.2.0", string.Join(',',
			Asset("OsmoOverlay-0.2.0-win-arm64-setup.exe", "sha256:" + new string('b', 64)),
			Asset("OsmoOverlay-0.2.0-win-x64-setup.exe", "sha256:" + new string('A', 64)),
			Asset("OsmoOverlay-0.2.0-win-x64-self-contained.zip")));

		(AppRelease release, var sumsUrl) = AppUpdates.ParseRelease(json, Suffix);

		Assert.AreEqual(new Version(0, 2, 0), release.Version);
		Assert.AreEqual("OsmoOverlay-0.2.0-win-x64-setup.exe", release.Installer?.Name);
		Assert.AreEqual(new string('a', 64), release.Installer?.Sha256);
		Assert.AreEqual(1234, release.Installer?.Size);
		Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), release.PublishedAt);
		Assert.IsNull(sumsUrl);
	}

	[TestMethod]
	public void ParseRelease_WithoutDigest_PointsAtTheSumsFile()
	{
		JsonNode json = Release("0.2.0", string.Join(',',
			Asset("OsmoOverlay-0.2.0-win-x64-setup.exe"),
			Asset("OsmoOverlay-0.2.0-SHA256SUMS.txt")));

		(AppRelease release, var sumsUrl) = AppUpdates.ParseRelease(json, Suffix);

		Assert.IsNull(release.Installer?.Sha256);
		Assert.AreEqual("https://example.test/OsmoOverlay-0.2.0-SHA256SUMS.txt", sumsUrl);
	}

	[TestMethod]
	public void ParseRelease_NoInstallerForThisMachine_StillGivesTheVersion()
	{
		(AppRelease release, _) = AppUpdates.ParseRelease(Release("v1.4.2-beta", Asset("OsmoOverlay-1.4.2-linux-x64.tar.gz")), Suffix);

		Assert.AreEqual(new Version(1, 4, 2), release.Version);
		Assert.IsNull(release.Installer);
	}

	[TestMethod]
	public void ParseRelease_TagThatIsNotAVersion_Throws()
	{
		Assert.ThrowsExactly<InvalidDataException>(() => AppUpdates.ParseRelease(Release("latest-build", ""), Suffix));
	}

	[TestMethod]
	public void FindChecksum_ReadsSha256sumLines()
	{
		const string sums = """
		                    4E7C1820F503A9F601D412272DBEB9DE4009F8E5B76F9AEB08FB04DE80FD8DBE  OsmoOverlay-0.2.0-win-x64-self-contained.zip
		                    1ecb9d12135baf762e30a948250c83e2f2bbe28dc5420a22775d15e3af9da54b *OsmoOverlay-0.2.0-win-x64-setup.exe
		                    """;

		Assert.AreEqual("1ecb9d12135baf762e30a948250c83e2f2bbe28dc5420a22775d15e3af9da54b",
			AppUpdates.FindChecksum(sums.ReplaceLineEndings("\r\n"), "OsmoOverlay-0.2.0-win-x64-setup.exe"));
		Assert.AreEqual("4e7c1820f503a9f601d412272dbeb9de4009f8e5b76f9aeb08fb04de80fd8dbe",
			AppUpdates.FindChecksum(sums, "OsmoOverlay-0.2.0-win-x64-self-contained.zip"));
		Assert.IsNull(AppUpdates.FindChecksum(sums, "OsmoOverlay-0.2.0-win-arm64-setup.exe"));
	}
}
