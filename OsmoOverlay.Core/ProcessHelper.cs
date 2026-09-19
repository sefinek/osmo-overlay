using System.Diagnostics;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core;

internal static class ProcessHelper
{
	public static ProcessStartInfo CreateHidden(string command, params string[] args)
	{
		return Create(command, args, true, true, false);
	}

	/// <summary>
	///     Like CreateHidden, but for a command that's spawned at high frequency for routine, uninteresting
	///     work (e.g. one ffmpeg process per frame while scrubbing a preview) - still reaches the file log
	///     (AppLogger.Info, for debugging a broken preview), but skips AppLogger.Notify so it doesn't flood
	///     the GUI's log panel the way a scrub session would if every frame grab surfaced there.
	/// </summary>
	public static ProcessStartInfo CreateHiddenQuiet(string command, params string[] args)
	{
		return Create(command, args, false, true, false);
	}

	/// <summary>Like CreateHidden, but pipes into stdin instead of redirecting stdout - for a tool that's fed data (e.g. ffmpeg's overlay compositing, which reads raw frames from stdin) rather than one whose stdout is read.</summary>
	public static ProcessStartInfo CreateHiddenWithStdin(string command, IEnumerable<string> args)
	{
		return Create(command, args, true, false, true);
	}

	private static ProcessStartInfo Create(string command, IEnumerable<string> args, bool notify, bool redirectStandardOutput, bool redirectStandardInput)
	{
		IReadOnlyCollection<string> argList = args as IReadOnlyCollection<string> ?? args.ToList();
		var psi = new ProcessStartInfo(command)
		{
			RedirectStandardOutput = redirectStandardOutput,
			RedirectStandardInput = redirectStandardInput,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Hidden
		};
		foreach (var arg in argList) psi.ArgumentList.Add(arg);

		var line = $"Running: {FormatCommand(command, argList)}";
		if (notify) AppLogger.Notify(line);
		else AppLogger.Info(line);

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
