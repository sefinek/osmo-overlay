using OsmoOverlay.Core;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Logging;

string[] options = ["-o", "--frames", "--from", "--to", "--cut"];
const string usage = "Usage: OsmoOverlay.Cli <input1.mp4> [input2.mp4 ...] [-o <output.mp4>] [--frames N] [--from <time>] [--to <time>] [--cut <time>-<time> ...]\n" +
                     "  <time> is seconds (90, 90.5) or [h:]mm:ss[.fff] (1:30, 1:02:03.25) on the combined timeline of all inputs\n" +
                     "  --cut removes that part from the video and the telemetry; repeat it for several cuts\n" +
                     "       OsmoOverlay.Cli --install-dependencies [ffmpeg] [exiftool]\n" +
                     "  installs the listed tools (default: ffmpeg) through the system's package manager, if they're missing";

if (args.Length == 0)
{
	Console.WriteLine(usage);
	return 1;
}

if (args[0] == "--install-dependencies")
	return await InstallDependenciesAsync(args[1..]);

var inputPaths = new List<string>();
var outputPath = "";
int? frameLimit = null;
double? rangeStart = null;
double? rangeEnd = null;
List<TimeRange> cutOuts = [];

var i = 0;
while (i < args.Length && !options.Contains(args[i]))
	inputPaths.Add(args[i++]);

if (inputPaths.Count == 0)
{
	Console.Error.WriteLine("Error: at least one input file is required");
	return 1;
}

for (; i < args.Length; i++)
{
	if (!options.Contains(args[i])) continue;

	if (i + 1 >= args.Length)
	{
		Console.Error.WriteLine($"Error: missing value for {args[i]}");
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
		case "--from" when TimeText.TryParse(value, out var from):
			rangeStart = from;
			break;
		case "--to" when TimeText.TryParse(value, out var to):
			rangeEnd = to;
			break;
		case "--cut" when value.Split('-') is [var cutFrom, var cutTo] &&
		                  TimeText.TryParse(cutFrom, out var cutStart) && TimeText.TryParse(cutTo, out var cutEnd) && cutEnd > cutStart:
			cutOuts.Add(new TimeRange(cutStart, cutEnd));
			break;
		default:
			Console.Error.WriteLine($"Error: invalid value for {option}: '{value}'");
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
	new RenderOptions(inputPaths, outputPath, frameLimit, RangeStartSeconds: rangeStart, RangeEndSeconds: rangeEnd, CutOuts: cutOuts), progress,
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

// Run by the Windows installer (OsmoOverlay.iss) - the same install path the GUI's dependency prompt takes, so it only
// installs what's missing and keeps FFmpeg on the major the preview supports.
static async Task<int> InstallDependenciesAsync(string[] names)
{
	List<ExternalTool> tools = names.Length == 0
		? [RequiredTools.Ffmpeg]
		: [.. RequiredTools.All.Where(t => names.Contains(t.DisplayName, StringComparer.OrdinalIgnoreCase))];
	if (tools.Count != Math.Max(1, names.Length))
	{
		Console.Error.WriteLine($"Error: unknown tool - expected {string.Join(" or ", RequiredTools.All.Select(t => t.DisplayName.ToLowerInvariant()))}");
		return 1;
	}

	IReadOnlyList<ExternalTool> missing = DependencyChecker.FindMissing(tools);
	if (missing.Count == 0)
	{
		Console.WriteLine("All dependencies are already installed");
		return 0;
	}

	var failed = false;
	foreach (ExternalTool tool in missing)
	{
		Console.WriteLine($"Installing {tool.DisplayName}...");
		InstallResult result = await DependencyInstaller.InstallAsync(tool, Console.WriteLine, CancellationToken.None);
		Console.WriteLine(result.Message);
		failed |= !result.Success;
	}

	return failed ? 1 : 0;
}
