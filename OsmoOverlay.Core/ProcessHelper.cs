using System.Diagnostics;

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
		return psi;
	}
}
