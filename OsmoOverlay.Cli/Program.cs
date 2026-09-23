using System.Globalization;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Logging;

string[] options = ["-o", "--frames", "--from", "--to"];
const string usage = "Usage: OsmoOverlay.Cli <input1.mp4> [input2.mp4 ...] [-o <output.mp4>] [--frames N] [--from <time>] [--to <time>]\n" +
                     "  <time> is seconds (90, 90.5) or [h:]mm:ss[.fff] (1:30, 1:02:03.25) on the combined timeline of all inputs";

if (args.Length == 0)
{
	Console.WriteLine(usage);
	return 1;
}

var inputPaths = new List<string>();
var outputPath = "";
int? frameLimit = null;
double? rangeStart = null;
double? rangeEnd = null;

var i = 0;
while (i < args.Length && !options.Contains(args[i]))
	inputPaths.Add(args[i++]);

if (inputPaths.Count == 0)
{
	Console.Error.WriteLine("Error: at least one input file is required.");
	return 1;
}

for (; i < args.Length; i++)
{
	if (!options.Contains(args[i])) continue;

	if (i + 1 >= args.Length)
	{
		Console.Error.WriteLine($"Error: missing value for {args[i]}.");
		return 1;
	}

	var option = args[i];
	var value = args[++i];
	switch (option)
	{
		case "-o":
			outputPath = value;
			break;
		case "--frames" when int.TryParse(value, out var parsedFrameLimit) && parsedFrameLimit > 0:
			frameLimit = parsedFrameLimit;
			break;
		case "--from" when TryParseTime(value, out var from):
			rangeStart = from;
			break;
		case "--to" when TryParseTime(value, out var to):
			rangeEnd = to;
			break;
		default:
			Console.Error.WriteLine($"Error: invalid value for {option}: '{value}'.");
			return 1;
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

RenderResult result = await RenderJob.RunAsync(
	new RenderOptions(inputPaths, outputPath, frameLimit, RangeStartSeconds: rangeStart, RangeEndSeconds: rangeEnd), progress,
	CancellationToken.None);
Console.WriteLine();

if (!result.Success)
{
	// No AppLogger.Error call here - RenderJob already logged this failure to the file log.
	Console.Error.WriteLine($"Error: {result.ErrorMessage}");
	return 1;
}

var doneMessage = $"Done: {outputPath} (render time: {result.Elapsed:hh\\:mm\\:ss})";
Console.WriteLine(doneMessage);
AppLogger.Info(doneMessage);
return 0;

// Seconds ("90", "90.5") or [h:]mm:ss[.fff] ("1:30", "1:02:03.25").
static bool TryParseTime(string text, out double seconds)
{
	seconds = 0;
	foreach (var part in text.Split(':'))
	{
		if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0) return false;
		seconds = seconds * 60 + value;
	}

	return text.Split(':').Length <= 3;
}
