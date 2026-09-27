using System.Reflection;
using System.Runtime.InteropServices;

namespace OsmoOverlay.Core;

/// <summary>Startup banner - the GUI's LOG panel shows it, the CLI writes it to its log. The environment line is what a bug report needs first.</summary>
public static class AppBanner
{
	public static IReadOnlyList<string> BuildLines(string appLabel)
	{
		string appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
		string coreVersion = typeof(RenderJob).Assembly.GetName().Version?.ToString(3) ?? "?";

		return
		[
			"==================================================",
			$"     OsmoOverlay | {appLabel} v{appVersion} | Core v{coreVersion}",
			" Telemetry HUD burner for DJI Osmo Action footage",
			$" {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}",
			"=================================================="
		];
	}
}
