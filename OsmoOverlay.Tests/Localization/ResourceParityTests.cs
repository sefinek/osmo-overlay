using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OsmoOverlay.Tests.Localization;

/// <summary>Every neutral (English) .resx in the repository against its Polish copy - the same keys, placeholders and plural shape.</summary>
[TestClass]
public sealed partial class ResourceParityTests
{
	public static IEnumerable<object[]> NeutralFiles()
	{
		return Directory.EnumerateFiles(RepositoryRoot(), "*.resx", SearchOption.AllDirectories)
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
			            !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
			.Where(f => Path.GetFileNameWithoutExtension(f).IndexOf('.') < 0)
			.Select(f => new object[] { Path.GetRelativePath(RepositoryRoot(), f) });
	}

	[TestMethod]
	public void RepositoryHasResources()
	{
		Assert.IsTrue(NeutralFiles().Count() >= 2);
	}

	[TestMethod]
	[DynamicData(nameof(NeutralFiles))]
	public void PolishMatchesEnglish(string relativePath)
	{
		string neutral = Path.Combine(RepositoryRoot(), relativePath);
		string polish = Path.ChangeExtension(neutral, ".pl.resx");
		Assert.IsTrue(File.Exists(polish), $"{relativePath} has no Polish copy");

		Dictionary<string, string> en = Read(neutral);
		Dictionary<string, string> pl = Read(polish);
		CollectionAssert.AreEquivalent(en.Keys.ToList(), pl.Keys.ToList(), relativePath);

		foreach ((string key, string english) in en)
		{
			string translated = pl[key];
			Assert.IsFalse(string.IsNullOrWhiteSpace(english), $"{key} is empty in English");
			Assert.IsFalse(string.IsNullOrWhiteSpace(translated), $"{key} is empty in Polish");
			Assert.IsFalse(english.Contains("\\n") || translated.Contains("\\n"), $"{key}: resx doesn't unescape \\n, use a real line break");
			CollectionAssert.AreEquivalent(Placeholders(english), Placeholders(translated), $"{key}: placeholders differ");
			Assert.AreEqual(char.IsWhiteSpace(english[0]), char.IsWhiteSpace(translated[0]), $"{key}: leading whitespace differs");
			Assert.AreEqual(char.IsWhiteSpace(english[^1]), char.IsWhiteSpace(translated[^1]), $"{key}: trailing whitespace differs");

			int englishForms = english.Split('|').Length;
			int polishForms = translated.Split('|').Length;
			if (englishForms > 1)
			{
				Assert.AreEqual(2, englishForms, $"{key}: English plural takes one|other");
				Assert.AreEqual(3, polishForms, $"{key}: Polish plural takes one|few|many");
			}
			else Assert.AreEqual(1, polishForms, $"{key}: Polish has plural forms the English doesn't");
		}
	}

	private static Dictionary<string, string> Read(string path)
	{
		return XDocument.Load(path).Root!.Elements("data")
			.ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");
	}

	private static List<string> Placeholders(string text)
	{
		return [.. PlaceholderPattern().Matches(text).Select(m => m.Groups[1].Value).Distinct()];
	}

	private static string RepositoryRoot()
	{
		for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			if (File.Exists(Path.Combine(dir.FullName, "OsmoOverlay.slnx"))) return dir.FullName;
		}

		throw new DirectoryNotFoundException("OsmoOverlay.slnx not found above the test output.");
	}

	[GeneratedRegex(@"\{(\d+)")]
	private static partial Regex PlaceholderPattern();
}
