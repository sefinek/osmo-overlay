using System.Reflection;

namespace OsmoOverlay.Core;

public static class AppCommand
{
	/// <summary>What starts this app again: its own executable, or `dotnet app.dll` when it runs through the muxer.</summary>
	public static (string Path, string[] Args) Current()
	{
		var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("The app's executable path is unknown");
		return Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
		       Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entryAssembly
			? (processPath, [entryAssembly])
			: (processPath, []);
	}
}
