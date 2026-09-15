using System.Diagnostics;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core;

internal static class ProcessHelper
{
	public static ProcessStartInfo CreateHidden(string command, params string[] args)
	{
		var psi = new ProcessStartInfo(command)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden
		};
		foreach (var arg in args) psi.ArgumentList.Add(arg);

		AppLogger.Info($"Running: {FormatCommand(command, args)}");
		return psi;
	}

	/// <summary>Quotes only the args that need it, so the logged line stays readable but still pastable into a shell.</summary>
	public static string FormatCommand(string command, IEnumerable<string> args)
	{
		return string.Join(' ', new[] { command }.Concat(args).Select(QuoteIfNeeded));
	}

	private static string QuoteIfNeeded(string arg)
	{
		return arg.Length == 0 || arg.Contains(' ') ? $"\"{arg}\"" : arg;
	}
}
