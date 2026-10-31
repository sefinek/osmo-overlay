namespace OsmoOverlay.Drones.Dji;

/// <summary>The AES key and the first IV of one feature point.</summary>
public readonly record struct FeatureKey(byte[] Key, byte[] Iv);

/// <summary>The keys of one recording session of a log, by feature point.</summary>
public sealed class Keychain(IReadOnlyDictionary<int, FeatureKey> keys)
{
	public IReadOnlyDictionary<int, FeatureKey> Keys { get; } = keys;

	internal FeatureKey? Find(int featurePoint)
	{
		return Keys.TryGetValue(featurePoint, out FeatureKey key) ? key : null;
	}
}

/// <summary>What is sent to DJI to get a log's keychains: the log's key blocks, one list per recording session.</summary>
public sealed record KeychainsRequest(ushort Version, byte Department, List<List<(int FeaturePoint, byte[] Data)>> Keychains);
