using OsmoOverlay.Core;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Tests.Support;

[TestClass]
public class SecretProtectorTests
{
	private const string Key = "AAPTaV4_example-key.with+odd/chars==";

	[TestMethod]
	public void Protect_HidesTheValueAndUnprotectGivesItBack()
	{
		string stored = SecretProtector.Protect(Key);

		Assert.IsTrue(SecretProtector.IsProtected(stored));
		Assert.IsFalse(stored.Contains("example", StringComparison.Ordinal));
		Assert.AreEqual(Key, SecretProtector.Unprotect(stored));
	}

	[TestMethod]
	public void Protect_IsNotRepeatable_ButAlreadyProtectedStaysAsItIs()
	{
		string first = SecretProtector.Protect(Key);

		Assert.AreNotEqual(first, SecretProtector.Protect(Key), "a fresh nonce each time");
		Assert.AreEqual(first, SecretProtector.Protect(first));
	}

	[TestMethod]
	public void Unprotect_AValueFromBeforeIsReturnedAsItIs()
	{
		Assert.AreEqual("plain-key", SecretProtector.Unprotect("plain-key"));
		Assert.AreEqual("", SecretProtector.Protect(""));
	}

	[TestMethod]
	public void Unprotect_ADamagedValueIsNull()
	{
		string stored = SecretProtector.Protect(Key);
		string damaged = stored[..^6] + (stored[^6] == 'A' ? 'B' : 'A') + stored[^5..];

		Assert.IsNull(SecretProtector.Unprotect(damaged));
		Assert.IsNull(SecretProtector.Unprotect("enc1:not base64 !"));
	}

	[TestMethod]
	public void Settings_ProtectOnlyTheSecrets_AndRoundTrip()
	{
		var settings = new OverlaySettings
		{
			MapApiKeys = new Dictionary<string, string> { ["esri"] = Key, ["carto"] = "cb1_x" },
			CustomMapUrlTemplate = "https://tiles.example/{z}/{x}/{y}.png?key=secret",
			CustomMapAttribution = "(c) example",
			UiLanguage = "pl"
		};

		OverlaySettings onDisk = OverlaySettingsStore.Protect(settings);

		Assert.IsTrue(onDisk.MapApiKeys!.Values.All(SecretProtector.IsProtected));
		Assert.IsTrue(SecretProtector.IsProtected(onDisk.CustomMapUrlTemplate!));
		Assert.AreEqual("(c) example", onDisk.CustomMapAttribution);
		Assert.AreEqual("pl", onDisk.UiLanguage);
		Assert.IsFalse(OverlaySettingsStore.HasPlainSecrets(onDisk));
		Assert.IsTrue(OverlaySettingsStore.HasPlainSecrets(settings));

		OverlaySettings back = OverlaySettingsStore.Unprotect(onDisk);
		CollectionAssert.AreEquivalent(settings.MapApiKeys, back.MapApiKeys);
		Assert.AreEqual(settings.CustomMapUrlTemplate, back.CustomMapUrlTemplate);
	}

	[TestMethod]
	public void Settings_WithoutSecretsStayAsTheyAre()
	{
		var settings = new OverlaySettings();

		Assert.IsFalse(OverlaySettingsStore.HasPlainSecrets(settings));
		Assert.IsNull(OverlaySettingsStore.Protect(settings).MapApiKeys);
		Assert.IsNull(OverlaySettingsStore.Protect(settings).CustomMapUrlTemplate);
	}
}
