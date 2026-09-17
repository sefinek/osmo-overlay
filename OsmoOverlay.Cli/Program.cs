using System.Reflection;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;

PrintBanner();

if (args.Length == 0)
{
	Console.WriteLine("Usage: OsmoOverlay.Cli <input1.mp4> [input2.mp4 ...] [-o <output.mp4>] [--frames N]");
	return 1;
}

var inputPaths = new List<string>();
var outputPath = "";
int? frameLimit = null;

var i = 0;
while (i < args.Length && args[i] is not ("-o" or "--frames"))
	inputPaths.Add(args[i++]);

if (inputPaths.Count == 0)
{
	Console.Error.WriteLine("Error: at least one input file is required.");
	return 1;
}

for (; i < args.Length; i++)
{
	if (args[i] is not ("-o" or "--frames")) continue;

	if (i + 1 >= args.Length)
	{
		Console.Error.WriteLine($"Error: missing value for {args[i]}.");
		return 1;
	}

	if (args[i] == "-o")
	{
		outputPath = args[++i];
	}
	else
	{
		if (!int.TryParse(args[i + 1], out var parsedFrameLimit))
		{
			Console.Error.WriteLine($"Error: --frames expects a number, got '{args[i + 1]}'.");
			return 1;
		}

		frameLimit = parsedFrameLimit;
		i++;
	}
}

if (string.IsNullOrEmpty(outputPath))
	outputPath = RenderOptions.DefaultOutputPath(inputPaths);

var progress = new Progress<RenderStatus>(status =>
{
	if (status is { Phase: RenderPhase.Rendering, TotalFrames: > 0 } &&
	    (status.Message.StartsWith("Frame") || status.Message.StartsWith("Fetching map tiles:")))
	{
		var pct = 100.0 * status.CurrentFrame / status.TotalFrames;
		Console.Write($"\r  {status.Message} ({pct:0.0}%) - {status.Elapsed:hh\\:mm\\:ss}   ");
	}
	else
	{
		Console.WriteLine(status.Message);
		AppLogger.Info(status.Message);
	}
});

RenderResult result = await RenderJob.RunAsync(new RenderOptions(inputPaths, outputPath, frameLimit), progress,
	CancellationToken.None);
Console.WriteLine();

if (!result.Success)
{
	Console.Error.WriteLine($"Error: {result.ErrorMessage}");
	AppLogger.Error(new InvalidOperationException(result.ErrorMessage), "Render failed");
	return 1;
}

var doneMessage = $"Done: {outputPath} (render time: {result.Elapsed:hh\\:mm\\:ss})";
Console.WriteLine(doneMessage);
AppLogger.Info(doneMessage);
return 0;

// Only the CLI's own and Core's assembly versions are shown - the GUI is a separate, unreferenced
// executable, so its version isn't something this process can read without loading that assembly
// just for a version string.
static void PrintBanner()
{
	var cliVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
	var coreVersion = typeof(RenderJob).Assembly.GetName().Version?.ToString(3) ?? "?";

	Console.WriteLine("==================================================");
	Console.WriteLine("  OsmoOverlay CLI");
	Console.WriteLine("  Telemetry HUD burner for DJI Osmo Action footage");
	Console.WriteLine("==================================================");
	Console.WriteLine($"  CLI  v{cliVersion}");
	Console.WriteLine($"  Core v{coreVersion}");
	Console.WriteLine("==================================================");
	Console.WriteLine();
}
