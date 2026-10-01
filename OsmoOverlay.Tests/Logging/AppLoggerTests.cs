using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Tests.Logging;

[TestClass]
public sealed class AppLoggerTests
{
	private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
	private static readonly char Separator = Path.DirectorySeparatorChar;

	[TestMethod]
	public void TheHomeFolder_BecomesATilde()
	{
		Assert.AreEqual($"Input: ~{Separator}Videos{Separator}ride.mp4", AppLogger.Scrub($"Input: {Home}{Separator}Videos{Separator}ride.mp4"));
		Assert.AreEqual("Home is ~", AppLogger.Scrub($"Home is {Home}"));
		Assert.AreEqual("'~' and \"~\"", AppLogger.Scrub($"'{Home}' and \"{Home}\""));
	}

	[TestMethod]
	public void AFolderThatOnlyStartsLikeIt_StaysAsItIs()
	{
		string other = $"{Home}2{Separator}ride.mp4";
		string withDot = $"{Home}.old{Separator}ride.mp4";

		Assert.AreEqual(other, AppLogger.Scrub(other));
		Assert.AreEqual(withDot, AppLogger.Scrub(withDot));
	}

	[TestMethod]
	public void OnWindows_TheCaseDoesNotMatter()
	{
		if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Paths are case-sensitive here.");

		Assert.AreEqual($"~{Separator}x", AppLogger.Scrub($"{Home.ToUpperInvariant()}{Separator}x"));
	}
}
