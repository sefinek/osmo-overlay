using OsmoOverlay.Core;

if (args.Length == 0)
{
	Console.WriteLine("Usage: OsmoOverlay.Cli <input.mp4> [-o <output.mp4>] [--frames N]");
	return 1;
}

var inputPath = args[0];
var outputPath = "";
int? frameLimit = null;
for (var i = 1; i < args.Length; i++)
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
	outputPath = RenderOptions.DefaultOutputPath(inputPath);

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
	}
});

RenderResult result = await RenderJob.RunAsync(new RenderOptions(inputPath, outputPath, frameLimit), progress,
	CancellationToken.None);
Console.WriteLine();

if (!result.Success)
{
	Console.Error.WriteLine($"Error: {result.ErrorMessage}");
	return 1;
}

Console.WriteLine($"Done: {outputPath} (render time: {result.Elapsed:hh\\:mm\\:ss})");
return 0;
