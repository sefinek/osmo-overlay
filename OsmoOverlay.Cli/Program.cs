using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;

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

	if (args[i] == "-o") outputPath = args[++i];
	else frameLimit = int.Parse(args[++i]);
}

if (string.IsNullOrEmpty(outputPath))
	outputPath = RenderOptions.DefaultOutputPath(inputPaths);

var progress = new Progress<RenderStatus>(status =>
{
	if (status is { Phase: RenderPhase.Rendering, TotalFrames: > 0 } && status.Message.StartsWith("Frame"))
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

Console.WriteLine($"Done: {outputPath} (render time: {result.Elapsed:hh\\:mm\\:ss})");
AppLogger.Info($"Done: {outputPath} (render time: {result.Elapsed:hh\\:mm\\:ss})");
return 0;
