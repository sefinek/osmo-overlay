using System.Runtime.InteropServices;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Updates;

namespace OsmoOverlay.Core;

/// <summary>Startup banner - the GUI's LOG panel shows it, the CLI writes it to its log. The environment line is what a bug report needs first.</summary>
public static class AppBanner
{
	public static IReadOnlyList<string> BuildLines(string appLabel)
	{
		return
		[
			string.Format(CoreStrings.AppBanner_Version, AppUpdates.CurrentVersionLabel, appLabel, BuildInfo.Describe()),
			$"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}"
		];
	}
}
