using System.Reflection;

namespace OsmoOverlay.Core;

/// <summary>Shared startup banner text so the CLI console and the GUI's LOG panel show the same thing.</summary>
public static class AppBanner
{
	public static IReadOnlyList<string> BuildLines(string appLabel)
	{
		var appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
		var coreVersion = typeof(RenderJob).Assembly.GetName().Version?.ToString(3) ?? "?";

		return
		[
			"==================================================",
			"  OsmoOverlay",
			"  Telemetry HUD burner for DJI Osmo Action footage",
			"==================================================",
			$"  {appLabel} v{appVersion}",
			$"  Core v{coreVersion}",
			"=================================================="
		];
	}
}
