using OsmoOverlay.Cameras.Dji;
using OsmoOverlay.Cameras.Insta360;
using OsmoOverlay.Core;
using OsmoOverlay.Core.Cameras;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Localization;
using OsmoOverlay.Core.Logging;
using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Reframe;

UiLanguages.Apply(UiLanguages.English);

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
	if (e.ExceptionObject is Exception ex) AppLogger.Error(ex, $"Fatal error: {ex.Message}");
};
foreach (string line in AppBanner.BuildLines("CLI"))
	AppLogger.Info(line);

CameraFormats.Register(new DjiOsmoFormat(), new Insta360Format());

string[] options = ["-o", "--frames", "--from", "--to", "--cut", "--view"];
string[] flags = ["--no-level"];
const string usage = "Usage: OsmoOverlay.Cli <input1> [input2 ...] [-o <output.mp4>] [--frames N] [--from <time>] [--to <time>] [--cut <time>-<time> ...] [--view <yaw>,<pitch>,<roll>[,<fov>]] [--no-level]\n" +
                     "  inputs are the camera's own recordings (DJI Osmo Action .MP4)\n" +
                     "  <time> is seconds (90, 90.5) or [h:]mm:ss[.fff] (1:30, 1:02:03.25) on the combined timeline of all inputs\n" +
                     "  --cut removes that part from the video and the telemetry; repeat it for several cuts. Add @fade or @white (optionally =seconds, e.g. 1:00-1:10@fade=1) to fade through black or white where the cut joins the kept parts, or @cross, @wipeleft, @wiperight, @slideleft, @slideright to blend them (the output gets shorter by that length)\n" +
                     "  --view <yaw>,<pitch>,<roll>[,<fov>] frames a 360 recording's flat picture, in degrees (default 0,0,0,100),\n" +
                     "         leveled by the camera's accelerometer unless --no-level\n" +
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
string outputPath = "";
int? frameLimit = null;
double? rangeStart = null;
double? rangeEnd = null;
List<TimeRange> cutOuts = [];
ReframeView? view = null;
bool level = true;

int i = 0;
while (i < args.Length && !options.Contains(args[i]) && !flags.Contains(args[i]))
	inputPaths.Add(args[i++]);

if (inputPaths.Count == 0)
{
	Console.Error.WriteLine("Error: at least one input file is required");
	return 1;
}

for (; i < args.Length; i++)
{
	if (args[i] == "--no-level")
	{
		level = false;
		continue;
	}

	if (!options.Contains(args[i])) continue;

	if (i + 1 >= args.Length)
	{
		Console.Error.WriteLine($"Error: missing value for {args[i]}");
		return 1;
	}

	string option = args[i];
	string value = args[++i];
	switch (option)
	{
		case "-o":
			outputPath = value;
			break;
		case "--frames" when int.TryParse(value, out int parsedFrameLimit) && parsedFrameLimit > 0:
			frameLimit = parsedFrameLimit;
			break;
		case "--from" when TimeText.TryParse(value, out double from):
			rangeStart = from;
			break;
		case "--to" when TimeText.TryParse(value, out double to):
			rangeEnd = to;
			break;
		case "--cut" when value.Split('@') is [var cutRange, .. var cutExtra] && cutExtra.Length <= 1 && cutRange.Split('-') is [var cutFrom, var cutTo] &&
		                  TimeText.TryParse(cutFrom, out double cutStart) && TimeText.TryParse(cutTo, out double cutEnd) && cutEnd > cutStart:
			CutTransition? transition = null;
			if (cutExtra.Length == 1)
			{
				if (!CutTransition.TryParse(cutExtra[0], out CutTransition parsedTransition))
				{
					Console.Error.WriteLine($"Error: invalid transition in --cut: '{cutExtra[0]}' (use fade, white, cross, wipeleft, wiperight, slideleft or slideright, optionally with =seconds)");
					return 1;
				}

				transition = parsedTransition;
			}

			cutOuts.Add(new TimeRange(cutStart, cutEnd, transition));
			break;
		case "--view" when ReframeView.TryParse(value, out ReframeView parsedView):
			view = parsedView;
			break;
		default:
			Console.Error.WriteLine($"Error: invalid value for {option}: '{value}'");
			return 1;
	}
}

if (string.IsNullOrEmpty(outputPath))
	outputPath = RenderOptions.DefaultOutputPath(inputPaths, OverlaySettingsStore.Load().DefaultOutputFolder);

var progress = new Progress<RenderStatus>(status =>
{
	if (status is { Phase: RenderPhase.Rendering, TotalFrames: > 0 } &&
	    (status.Message.StartsWith("Frame") || status.Message.StartsWith("Fetching map tiles:")))
	{
		double pct = 100.0 * status.CurrentFrame / status.TotalFrames;
		Console.Write($"\r  {status.Message} ({pct:0.0}%) - {status.Elapsed:hh\\:mm\\:ss}   ");
	}
	else
	{
		Console.WriteLine(status.Message);
		AppLogger.Info(status.Message);
	}
});

// Ctrl+C goes through RenderJob's cancellation, which stops ffmpeg and removes the unfinished output - ending the
// process outright would leave both behind.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
	e.Cancel = true;
	cts.Cancel();
};

RenderResult result = await RenderJob.RunAsync(
	new RenderOptions(inputPaths, outputPath, frameLimit, RangeStartSeconds: rangeStart, RangeEndSeconds: rangeEnd, CutOuts: cutOuts, Reframe: (view ?? new ReframeView()) with { Level = level }), progress,
	cts.Token);
Console.WriteLine();

if (!result.Success)
{
	// No AppLogger.Error call here - RenderJob already logged this failure to the file log.
	Console.Error.WriteLine($"Error: {result.ErrorMessage}");
	return 1;
}

string doneMessage = $"Done: {outputPath} (render time: {result.Elapsed:hh\\:mm\\:ss})";
Console.WriteLine(doneMessage);
AppLogger.Info(doneMessage);
return 0;

// The same install path the GUI's dependency prompt takes, so it only installs what's missing and keeps FFmpeg on the
// major the preview supports.
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

	bool failed = false;
	foreach (ExternalTool tool in missing)
	{
		Console.WriteLine($"Installing {tool.DisplayName}...");
		InstallResult result = await DependencyInstaller.InstallAsync(tool, Console.WriteLine, CancellationToken.None);
		Console.WriteLine(result.Message);
		failed |= !result.Success;
	}

	return failed ? 1 : 0;
}
