using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NuGet.Versioning;
using Telemavix.Core.Updates;

namespace Telemavix.Tests.Updates;

/// <summary>RemoteAccess.Evaluate on answers signed the way the API signs them (ECDSA P-256, SHA-256, IEEE P1363).</summary>
[TestClass]
public sealed class RemoteAccessTests
{
	private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
	private static readonly NuGetVersion Current = NuGetVersion.Parse("0.5.0");

	private static string Answer(ECDsa key, string? minVersion, string? url = null, DateTimeOffset? expiresAt = null, string? payloadOverride = null)
	{
		string payload = payloadOverride ?? JsonSerializer.Serialize(new
		{
			minVersion, url, issuedAt = Now.AddHours(-1), expiresAt = expiresAt ?? Now.AddHours(23)
		});
		byte[] signature = key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
		return JsonSerializer.Serialize(new { success = true, payload, signature = Convert.ToBase64String(signature) });
	}

	[TestMethod]
	public void NoMinimum_IsAllowed()
	{
		using ECDsa key = ECDsa.Create();
		Assert.IsFalse(RemoteAccess.Evaluate(Answer(key, null), key, Current, Now, true).Blocked);
	}

	[TestMethod]
	public void BelowMinimum_IsBlocked_WithItsUrl()
	{
		using ECDsa key = ECDsa.Create();
		AccessDecision decision = RemoteAccess.Evaluate(Answer(key, "0.6.0", "https://example.test/get"), key, Current, Now, true);
		Assert.IsTrue(decision.Blocked);
		Assert.AreEqual(NuGetVersion.Parse("0.6.0"), decision.MinVersion);
		Assert.AreEqual("https://example.test/get", decision.Url);
	}

	[TestMethod]
	public void AtOrAboveMinimum_IsAllowed()
	{
		using ECDsa key = ECDsa.Create();
		Assert.IsFalse(RemoteAccess.Evaluate(Answer(key, "0.5.0"), key, Current, Now, true).Blocked);
		Assert.IsFalse(RemoteAccess.Evaluate(Answer(key, "0.4.0"), key, Current, Now, true).Blocked);
	}

	[TestMethod]
	public void NonHttpsUrl_IsDropped()
	{
		using ECDsa key = ECDsa.Create();
		Assert.IsNull(RemoteAccess.Evaluate(Answer(key, "0.6.0", "file:///c:/windows/system32/calc.exe"), key, Current, Now, true).Url);
		Assert.IsNull(RemoteAccess.Evaluate(Answer(key, "0.6.0", "http://example.test"), key, Current, Now, true).Url);
	}

	[TestMethod]
	public void SignedWithAnotherKey_Throws()
	{
		using ECDsa trusted = ECDsa.Create();
		using ECDsa attacker = ECDsa.Create();
		Assert.ThrowsExactly<InvalidDataException>(() => RemoteAccess.Evaluate(Answer(attacker, "99.0.0"), trusted, Current, Now, true));
	}

	[TestMethod]
	public void ChangedPayload_Throws()
	{
		using ECDsa key = ECDsa.Create();
		string signed = Answer(key, null);
		string tampered = signed.Replace("\\u0022minVersion\\u0022:null", "\\u0022minVersion\\u0022:\\u002299.0.0\\u0022");
		Assert.AreNotEqual(signed, tampered);
		Assert.ThrowsExactly<InvalidDataException>(() => RemoteAccess.Evaluate(tampered, key, Current, Now, true));
	}

	[TestMethod]
	public void Expired_IsRefusedWhenFresh_ButKeptAnswerStillCounts()
	{
		using ECDsa key = ECDsa.Create();
		string old = Answer(key, "0.6.0", expiresAt: Now.AddDays(-3));
		Assert.ThrowsExactly<InvalidDataException>(() => RemoteAccess.Evaluate(old, key, Current, Now, true));
		Assert.IsTrue(RemoteAccess.Evaluate(old, key, Current, Now, false).Blocked);
	}

	[TestMethod]
	public void MissingParts_Throw()
	{
		using ECDsa key = ECDsa.Create();
		Assert.ThrowsExactly<InvalidDataException>(() => RemoteAccess.Evaluate("""{ "success": true }""", key, Current, Now, true));
		Assert.ThrowsExactly<InvalidDataException>(() => RemoteAccess.Evaluate("""{ "payload": "{}" }""", key, Current, Now, true));
	}

	[TestMethod]
	public void AnyOfTheTrustedKeys_Signs()
	{
		using ECDsa first = ECDsa.Create();
		using ECDsa spare = ECDsa.Create();
		using ECDsa other = ECDsa.Create();
		Assert.IsTrue(RemoteAccess.Evaluate(Answer(spare, "0.6.0"), [first, spare], Current, Now, true).Blocked);
		Assert.ThrowsExactly<InvalidDataException>(() => RemoteAccess.Evaluate(Answer(other, "0.6.0"), [first, spare], Current, Now, true));
	}

	[TestMethod]
	public void Decision_CarriesWhenItWasIssued()
	{
		using ECDsa key = ECDsa.Create();
		Assert.AreEqual(Now.AddHours(-1), RemoteAccess.Evaluate(Answer(key, null), key, Current, Now, true).IssuedAt);
		Assert.AreEqual(Now.AddHours(-1), RemoteAccess.Evaluate(Answer(key, "0.6.0"), key, Current, Now, true).IssuedAt);
	}

	[TestMethod]
	public void IsReplay_OnlyForAnAnswerOlderThanTheKeptOne()
	{
		AccessDecision At(DateTimeOffset when) => new(false, null, null, when);
		Assert.IsTrue(RemoteAccess.IsReplay(At(Now.AddHours(-3)), At(Now.AddHours(-1)), Now));
		Assert.IsFalse(RemoteAccess.IsReplay(At(Now.AddHours(-1)), At(Now.AddHours(-1)), Now));
		Assert.IsFalse(RemoteAccess.IsReplay(At(Now), At(Now.AddHours(-1)), Now));
		Assert.IsFalse(RemoteAccess.IsReplay(At(Now), AccessDecision.Allowed, Now));
	}

	[TestMethod]
	public void IsReplay_IgnoresAKeptAnswerDatedFarAhead()
	{
		AccessDecision At(DateTimeOffset when) => new(false, null, null, when);
		Assert.IsFalse(RemoteAccess.IsReplay(At(Now), At(Now.AddDays(30)), Now));
	}

	[TestMethod]
	public void ReleasePublicKeys_AreValidP256Keys()
	{
		Assert.HasCount(2, RemoteAccess.PublicKeys);
		foreach (string key in RemoteAccess.PublicKeys)
		{
			using ECDsa ecdsa = ECDsa.Create();
			ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
			Assert.AreEqual(256, ecdsa.KeySize);
		}
	}
}
