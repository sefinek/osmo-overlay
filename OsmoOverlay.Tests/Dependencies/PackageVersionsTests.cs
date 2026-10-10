using System.Xml.Linq;

namespace OsmoOverlay.Tests.Dependencies;

/// <summary>
///     Directory.Packages.props holds every version written out: Visual Studio's NuGet manager replaces a $(property) in a
///     Version with the number it updated to, so a shared property drifted apart from the packages that once used it.
///     Instead, a family's packages must simply carry the same version - updating one of them alone fails here.
/// </summary>
[TestClass]
public sealed class PackageVersionsTests
{
	private static readonly string[] Families = ["Avalonia", "SkiaSharp"];

	[TestMethod]
	public void EveryPackageFamily_SharesOneVersion()
	{
		Dictionary<string, string> versions = PackageVersions();

		foreach (string family in Families)
		{
			var members = versions.Where(p => p.Key == family || p.Key.StartsWith(family + ".", StringComparison.Ordinal)).ToList();
			Assert.IsTrue(members.Count > 1, $"{family}: fewer than two packages");
			Assert.AreEqual(1, members.Select(p => p.Value).Distinct().Count(),
				$"{family}: {string.Join(", ", members.Select(p => $"{p.Key} {p.Value}"))}");
		}
	}

	[TestMethod]
	public void EveryVersion_IsWrittenOut()
	{
		foreach ((string package, string version) in PackageVersions())
			Assert.IsFalse(version.Contains("$("), $"{package}: {version}");
	}

	private static Dictionary<string, string> PackageVersions()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
			directory = directory.Parent;
		Assert.IsNotNull(directory, "Directory.Packages.props not found above the test's folder");

		return XDocument.Load(Path.Combine(directory.FullName, "Directory.Packages.props"))
			.Descendants("PackageVersion")
			.ToDictionary(p => (string)p.Attribute("Include")!, p => (string)p.Attribute("Version")!);
	}
}
