using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using OsmoOverlay.Core.Dependencies;
using OsmoOverlay.Core.Updates;

namespace OsmoOverlay.Core;

/// <summary>What the benchmark ran on - for the report, and to tell a result from another computer's.</summary>
public sealed record BenchmarkSystem(string Os, string? Cpu, int Threads, long MemoryBytes, IReadOnlyList<string> Gpus, string? Ffmpeg, string App);

/// <summary>
///     How busy the computer is, 0-1 (null where it can't be read: the CPU on macOS, the GPU outside Windows without nvidia-smi),
///     and how many other programs encode video on the GPU (OBS, GeForce's or Radeon's recording, Discord, another render). A
///     game or another render running takes from every number the benchmark gives, and slows a render down.
/// </summary>
public sealed record BenchmarkLoad(double? Cpu, double? Gpu, double? Encoder = null, int? EncoderSessions = null)
{
	internal const double BusyThreshold = 0.2;
	// Before a render only a load that clearly slows it counts - a browser or GeForce's background recording mustn't ask every time.
	internal const double RenderCpuThreshold = 0.4;
	internal const double RenderGpuThreshold = 0.4;
	internal const double RenderEncoderThreshold = 0.15;
	// Encoding on the GPU, a render still draws the overlay (and ffmpeg lays it on) on the CPU - a busy one slows it too, if
	// less than it slows x264/x265.
	internal const double RenderCpuWithGpuEncoderThreshold = 0.6;

	/// <summary>Busy enough to skew the benchmark's numbers.</summary>
	public bool IsBusy => Cpu > BusyThreshold || Gpu > BusyThreshold || Encoder > BusyThreshold;

	/// <summary>The graphics card busy enough to slow a render encoding on it.</summary>
	public bool GpuSlowsRender => Encoder > RenderEncoderThreshold || Gpu > RenderGpuThreshold;

	/// <summary>The processor busy enough to slow a render with this kind of encoder.</summary>
	public bool CpuSlowsRender(bool gpuEncoder)
	{
		return Cpu > (gpuEncoder ? RenderCpuWithGpuEncoderThreshold : RenderCpuThreshold);
	}

	/// <summary>Busy enough to slow a render with this kind of encoder down noticeably: for a GPU encoder its card and the CPU, for x264/x265 the CPU.</summary>
	public bool SlowsRender(bool gpuEncoder)
	{
		return gpuEncoder && GpuSlowsRender || CpuSlowsRender(gpuEncoder);
	}
}

internal static partial class BenchmarkSystemInfo
{
	private const string DisplayAdapters = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

	public static async Task<BenchmarkSystem> CollectAsync(CancellationToken ct)
	{
		string? ffmpeg = await DependencyVersionChecker.GetInstalledVersionAsync(RequiredTools.Ffmpeg, ct);
		return new BenchmarkSystem(RuntimeInformation.OSDescription, CpuName(), Environment.ProcessorCount, MemoryBytes(),
			GpuNames(), ffmpeg, AppUpdates.CurrentVersion.ToString());
	}

	/// <summary>
	///     The CPU over one second, and within it the graphics card's load: of the card `gpuVendor` makes (PCI vendor id, the
	///     encoder's - FfmpegPipeline.GpuVendor), or the busiest one when null. On Windows from its performance counters, any
	///     vendor's (WindowsGpuLoad); elsewhere, or without them, from nvidia-smi - an NVIDIA card only.
	/// </summary>
	public static async Task<BenchmarkLoad> SampleLoadAsync(uint? gpuVendor, CancellationToken ct)
	{
		(long Idle, long Total)? before = CpuTimes();
		using WindowsGpuLoad.Sample? counters = OperatingSystem.IsWindows() ? WindowsGpuLoad.Sample.Start() : null;
		bool nvidia = counters is null && gpuVendor is null or NvidiaVendor;
		var clock = Stopwatch.StartNew();
		List<NvidiaSample> samples = [];
		for (int i = 0; nvidia && i < 3; i++)
		{
			if (await NvidiaUtilizationAsync(ct) is { } sample) samples.Add(sample);
			await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
		}

		if (clock.Elapsed < TimeSpan.FromSeconds(1)) await Task.Delay(TimeSpan.FromSeconds(1) - clock.Elapsed, ct);
		(long Idle, long Total)? after = CpuTimes();

		double? cpu = before is { } b && after is { } a && a.Total > b.Total ? Math.Clamp(1 - (double)(a.Idle - b.Idle) / (a.Total - b.Total), 0, 1) : null;
		if (counters?.Finish(cpu, gpuVendor) is { } load) return load;

		return samples.Count == 0
			? new BenchmarkLoad(cpu, null)
			: new BenchmarkLoad(cpu, samples.Average(s => s.Gpu), samples.Average(s => s.Encoder), samples.Max(s => s.Sessions));
	}

	private const uint NvidiaVendor = 0x10DE;

	private readonly record struct NvidiaSample(double Gpu, double Encoder, int Sessions);

	/// <summary>The busiest GPU's utilization and encoder use, from nvidia-smi - null without an NVIDIA card (or its driver's tool).</summary>
	private static async Task<NvidiaSample?> NvidiaUtilizationAsync(CancellationToken ct)
	{
		ProcessStartInfo psi = ProcessHelper.CreateHiddenQuiet("nvidia-smi", "--query-gpu=utilization.gpu,utilization.encoder,encoder.stats.sessionCount",
			"--format=csv,noheader,nounits");
		(int exitCode, string stdout, _) = await ProcessHelper.TryRunCapturedAsync(psi, ct);
		if (exitCode != 0) return null;

		NvidiaSample? busiest = null;
		foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			string[] fields = line.Split(',', StringSplitOptions.TrimEntries);
			if (fields.Length < 3 || !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double gpu)) continue;

			double encoder = double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double e) ? e : 0;
			int sessions = int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : 0;
			var sample = new NvidiaSample(gpu / 100, encoder / 100, sessions);
			if (busiest is not { } current || sample.Gpu + sample.Encoder > current.Gpu + current.Encoder) busiest = sample;
		}

		return busiest;
	}

	/// <summary>Idle and total CPU time since boot, in the platform's own units - only their differences mean anything.</summary>
	private static (long Idle, long Total)? CpuTimes()
	{
		if (OperatingSystem.IsWindows()) return GetSystemTimes(out long idle, out long kernel, out long user) ? (idle, kernel + user) : null;
		if (!OperatingSystem.IsLinux()) return null;

		try
		{
			// "cpu  user nice system idle iowait irq softirq steal ..." - idle and iowait are the time not spent working.
			long[] fields =
			[
				.. File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
					.Select(f => long.Parse(f, CultureInfo.InvariantCulture))
			];
			return (fields[3] + fields[4], fields.Sum());
		}
		catch (Exception ex) when (ex is IOException or FormatException or IndexOutOfRangeException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	///     The memory installed - on Windows the modules' own size; elsewhere what the system can use, a few percent less (the
	///     part the hardware and the kernel reserve), as is the fallback: 32 GB read that way as 31.2.
	/// </summary>
	private static long MemoryBytes()
	{
		if (OperatingSystem.IsWindows() && GetPhysicallyInstalledSystemMemory(out long kilobytes) && kilobytes > 0) return kilobytes * 1024;

		return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
	}

	private static string? CpuName()
	{
		try
		{
			if (OperatingSystem.IsWindows())
			{
				using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
				return (key?.GetValue("ProcessorNameString") as string)?.Trim();
			}

			if (OperatingSystem.IsLinux())
				return File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim();

			if (OperatingSystem.IsMacOS())
			{
				(int exitCode, string stdout, _) = ProcessHelper.RunCaptured(ProcessHelper.CreateHiddenQuiet("sysctl", "-n", "machdep.cpu.brand_string"));
				return exitCode == 0 ? stdout.Trim() : null;
			}
		}
		catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or InvalidOperationException
			                           or System.ComponentModel.Win32Exception)
		{
			// Only for the report.
		}

		return null;
	}

	/// <summary>The display adapters Windows lists, with their driver version - not its own basic or remote ones, nor a virtual monitor (VR headsets, streaming) - none on other systems.</summary>
	private static List<string> GpuNames()
	{
		return OperatingSystem.IsWindows() ? WindowsGpuNames() : [];
	}

	[SupportedOSPlatform("windows")]
	private static List<string> WindowsGpuNames()
	{
		List<string> names = [];
		try
		{
			using RegistryKey? adapters = Registry.LocalMachine.OpenSubKey(DisplayAdapters);
			foreach (string sub in adapters?.GetSubKeyNames() ?? [])
			{
				try
				{
					using RegistryKey? adapter = adapters!.OpenSubKey(sub);
					if (adapter?.GetValue("DriverDesc") is not string name || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
					    name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase) ||
					    name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
						continue;

					string entry = adapter.GetValue("DriverVersion") is string version ? $"{name} ({version})" : name;
					if (!names.Contains(entry)) names.Add(entry);
				}
				catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
				{
					// "Properties" and the like aren't readable - not adapters anyway.
				}
			}
		}
		catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
		{
			// Only for the report.
		}

		return names;
	}

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
