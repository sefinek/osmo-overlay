using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Tests.Preview;

[TestClass]
public sealed class WaveformCacheTests
{
	private string _directory = "";
	private List<PlaybackSegment> _segments = [];

	[TestInitialize]
	public void Setup()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"osmooverlay-waveform-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_directory);
		var video = Path.Combine(_directory, "clip.mp4");
		File.WriteAllBytes(video, [1, 2, 3]);
		_segments = [new PlaybackSegment(video, 3)];
	}

	[TestCleanup]
	public void Cleanup()
	{
		Directory.Delete(_directory, true);
	}

	[TestMethod]
	public void SavedPeaks_ComeBack()
	{
		float[][] peaks = [[0, 0.25f, 1], [0.5f, 0.001f, 0]];

		WaveformCache.Save(_segments, peaks, 1, _directory);
		var loaded = WaveformCache.TryLoad(_segments, _directory);

		Assert.IsNotNull(loaded);
		Assert.AreEqual(1f, loaded.Value.Loudest);
		for (var c = 0; c < peaks.Length; c++)
		for (var i = 0; i < peaks[c].Length; i++)
			Assert.AreEqual(peaks[c][i], loaded.Value.Peaks[c][i], 1e-4, $"channel {c}, bucket {i}");
	}

	[TestMethod]
	public void AChangedFile_IsDecodedAgain()
	{
		WaveformCache.Save(_segments, [[0.5f]], 0.5f, _directory);
		File.WriteAllBytes(_segments[0].Path, [1, 2, 3, 4]);

		Assert.IsNull(WaveformCache.TryLoad(_segments, _directory));
	}

	[TestMethod]
	public void AHeaderClaimingMoreThanTheFileHolds_IsAMiss()
	{
		WaveformCache.Save(_segments, [[0.5f, 0.25f]], 0.5f, _directory);
		var path = Directory.GetFiles(_directory, "*.waveform").Single();
		var bytes = File.ReadAllBytes(path);
		// Buckets sit right before Loudest and the peaks: 2 buckets of 1 channel, 4 + 4 + 2 * 2 bytes from the end.
		BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, bytes.Length - 12);
		File.WriteAllBytes(path, bytes);

		Assert.IsNull(WaveformCache.TryLoad(_segments, _directory));
	}

	[TestMethod]
	public void NothingSaved_IsAMiss()
	{
		Assert.IsNull(WaveformCache.TryLoad(_segments, _directory));
	}
}
