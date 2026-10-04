using OsmoOverlay.Core;

namespace OsmoOverlay.Tests.Recordings;

[TestClass]
public sealed class FileSummaryCacheTests
{
	private string _directory = "";

	[TestInitialize]
	public void Setup()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"osmooverlay-cache-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_directory);
	}

	[TestCleanup]
	public void Cleanup()
	{
		Directory.Delete(_directory, true);
	}

	private string CacheFile(string name, int daysUnused)
	{
		string path = Path.Combine(_directory, name);
		File.WriteAllText(path, "{}");
		File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysUnused));
		return path;
	}

	[TestMethod]
	public void DeleteUnused_RemovesOnlyEntriesNotUsedForThatLong()
	{
		string oldSummary = CacheFile("a.json.br", 31);
		string oldUncompressed = CacheFile("c.json", 31);
		string oldWaveform = CacheFile("a.waveform", 31);
		string fresh = CacheFile("b.json.br", 29);
		string other = CacheFile("notes.txt", 365);

		Assert.AreEqual(3, FileSummaryCache.DeleteUnused(TimeSpan.FromDays(30), _directory));

		Assert.IsFalse(File.Exists(oldSummary));
		Assert.IsFalse(File.Exists(oldWaveform));
		Assert.IsFalse(File.Exists(oldUncompressed));
		Assert.IsTrue(File.Exists(fresh));
		Assert.IsTrue(File.Exists(other), "only the cache's own files are touched");
	}

	[TestMethod]
	public void MarkUsed_KeepsAnEntryFromTheNextCleanup()
	{
		string summary = CacheFile("a.json", 31);

		FileSummaryCache.MarkUsed(summary);

		Assert.AreEqual(0, FileSummaryCache.DeleteUnused(TimeSpan.FromDays(30), _directory));
	}
}
