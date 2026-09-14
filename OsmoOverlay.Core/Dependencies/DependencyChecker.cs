using System.ComponentModel;
using System.Diagnostics;

namespace OsmoOverlay.Core.Dependencies;

public static class DependencyChecker
{
	public static bool IsCommandAvailable(string command)
	{
		try
		{
			var psi = new ProcessStartInfo(command)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			};

			using Process? process = Process.Start(psi);
			if (process is null) return false;

			if (!process.WaitForExit(3000)) process.Kill(true);
			return true;
		}
		catch (Win32Exception)
		{
			return false;
		}
	}

	public static bool IsAvailable(ExternalTool tool)
	{
		return tool.Commands.All(IsCommandAvailable);
	}

	public static async Task<IReadOnlyList<ExternalTool>> FindMissingAsync(IEnumerable<ExternalTool> tools)
	{
		List<ExternalTool> toolList = tools.ToList();
		var available = await Task.WhenAll(toolList.Select(t => Task.Run(() => IsAvailable(t))));
		return toolList.Where((_, i) => !available[i]).ToList();
	}
}
