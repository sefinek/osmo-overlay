using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Tests.Support;

[TestClass]
public sealed class ProcessHelperTests
{
	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[DoNotParallelize]
	public void CreateHidden_CommandIsOnlyShownInDebugMode(bool verbose)
	{
		bool original = AppLogger.Verbose;
		List<AppLogLevel> levels = [];
		void OnLog(string message, AppLogLevel level)
		{
			if (message.StartsWith("Running:", StringComparison.Ordinal)) levels.Add(level);
		}

		try
		{
			AppLogger.Verbose = verbose;
			AppLogger.Notified += OnLog;
			ProcessHelper.CreateHidden("ffmpeg", "-version");
			if (verbose) CollectionAssert.AreEqual(new[] { AppLogLevel.Debug }, levels);
			else Assert.IsEmpty(levels);
		}
		finally
		{
			AppLogger.Notified -= OnLog;
			AppLogger.Verbose = original;
		}
	}
}
