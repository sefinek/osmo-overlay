using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OsmoOverlay.Core;

/// <summary>
///     The GPU's load on Windows from its own performance counters (\GPU Engine(*)\Utilization Percentage, what Task Manager
///     shows) - every vendor's card, unlike nvidia-smi. Each counter instance is one process on one engine of one adapter
///     ("pid_1234_luid_0x0_0x12FBC_phys_0_eng_7_engtype_VideoEncode"); the adapter is told by its LUID, matched to one of
///     GpuAdapters'. This app's own process (its window drawing on the GPU) isn't counted.
/// </summary>
internal static unsafe partial class WindowsGpuLoad
{
	private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";
	private const uint PdhFmtDouble = 0x00000200;
	private const uint PdhFmtNoCap100 = 0x00008000;
	private const uint PdhMoreData = 0x800007D2;
	// PDH_CSTATUS_VALID_DATA and PDH_CSTATUS_NEW_DATA - both a good reading.
	private const uint PdhValidData = 0;
	private const uint PdhNewData = 1;
	private const uint MicrosoftVendor = 0x1414;
	// A process counts as encoding when it keeps the encoder busy this much of the time.
	private const double EncodingProcessShare = 0.01;

	/// <summary>The counters, read first when started and then by each <see cref="Read" /> - each reading the load since the one before.</summary>
	internal sealed class Sample : IDisposable
	{
		private readonly IntPtr _query;
		private readonly IntPtr _counter;

		private Sample(IntPtr query, IntPtr counter)
		{
			_query = query;
			_counter = counter;
		}

		public static Sample? Start()
		{
			try
			{
				if (PdhOpenQuery(IntPtr.Zero, 0, out IntPtr query) != 0) return null;
				if (PdhAddEnglishCounter(query, CounterPath, 0, out IntPtr counter) != 0 || PdhCollectQueryData(query) != 0)
				{
					PdhCloseQuery(query);
					return null;
				}

				return new Sample(query, counter);
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
			{
				return null;
			}
		}

		/// <summary>
		///     The load of the adapter an encoder of `vendor` (PCI vendor id) runs on (GpuAdapters.Pick), or of the busiest one when
		///     null - this app's own use left out unless `countThisApp`. Null when the counters couldn't be read, or there's no such
		///     adapter.
		/// </summary>
		public BenchmarkLoad? Read(double? cpu, uint? vendor, bool countThisApp)
		{
			if (PdhCollectQueryData(_query) != 0) return null;

			uint size = 0;
			uint status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out _, null);
			if (status != PdhMoreData || size == 0) return null;

			byte[] buffer = new byte[size];
			List<(string Name, double Percent)> readings = [];
			fixed (byte* items = buffer)
			{
				if (PdhGetFormattedCounterArray(_counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out uint count, items) != 0) return null;

				var item = (CounterItem*)items;
				for (int i = 0; i < count; i++)
				{
					if (item[i].Value.CStatus is PdhValidData or PdhNewData && Marshal.PtrToStringUni(item[i].Name) is { } name)
						readings.Add((name, item[i].Value.Value));
				}
			}

			return Aggregate(readings, countThisApp ? -1 : Environment.ProcessId, GpuAdapters.All, vendor, cpu);
		}

		public void Dispose()
		{
			PdhCloseQuery(_query);
		}
	}

	/// <summary>
	///     Per adapter: each engine's use summed over the processes (but `ownPid`); the GPU's load its busiest graphics or compute
	///     engine's (as nvidia-smi has it - the video engines have their own numbers, and the copy engines only carry frames), the
	///     encoder's its busiest encoding engine's - NVIDIA's and Intel's "VideoEncode", AMD's "Video Codec" (encoding and
	///     decoding) - the decoder's its busiest "VideoDecode", and the processes encoding. The adapter an encoder of `vendor` runs
	///     on (GpuAdapters.Pick; idle when nothing uses it), or the busiest real one (not Microsoft's software adapter) when null.
	/// </summary>
	internal static BenchmarkLoad? Aggregate(IEnumerable<(string Name, double Percent)> readings, int ownPid,
		IReadOnlyList<GpuAdapter> gpus, uint? vendor, double? cpu)
	{
		Dictionary<long, Dictionary<int, (double Percent, Engine Kind)>> adapters = [];
		Dictionary<long, HashSet<int>> encoding = [];
		foreach ((string name, double percent) in readings)
		{
			if (InstancePattern().Match(name) is not { Success: true } m) continue;

			int pid = int.Parse(m.Groups["pid"].Value, CultureInfo.InvariantCulture);
			long luid = Luid(m.Groups["high"].Value, m.Groups["low"].Value);
			int engine = int.Parse(m.Groups["eng"].Value, CultureInfo.InvariantCulture);
			Engine kind = KindOf(m.Groups["type"].Value);
			if (!adapters.TryGetValue(luid, out Dictionary<int, (double Percent, Engine Kind)>? engines)) adapters[luid] = engines = [];
			if (pid == ownPid || !double.IsFinite(percent) || percent <= 0)
			{
				engines.TryAdd(engine, (0, kind));
				continue;
			}

			engines[engine] = (engines.GetValueOrDefault(engine).Percent + percent, kind);
			if (kind == Engine.Encoder && percent / 100 >= EncodingProcessShare)
			{
				if (!encoding.TryGetValue(luid, out HashSet<int>? pids)) encoding[luid] = pids = [];
				pids.Add(pid);
			}
		}

		long? picked = vendor is { } v
			? GpuAdapters.Pick(gpus, v)?.Luid
			: adapters.Keys.Where(l => gpus.FirstOrDefault(g => g.Luid == l)?.VendorId != MicrosoftVendor)
				.Select(l => (long?)l).MaxBy(l => adapters[l!.Value].Values.Max(e => e.Percent));
		if (picked is not { } adapter) return null;
		if (!adapters.TryGetValue(adapter, out Dictionary<int, (double Percent, Engine Kind)>? load)) return new BenchmarkLoad(cpu, 0, 0, 0, 0);

		double? Busiest(Engine kind)
		{
			List<double> percents = [.. load.Values.Where(e => e.Kind == kind).Select(e => e.Percent)];
			return percents.Count == 0 ? null : Math.Clamp(percents.Max() / 100, 0, 1);
		}

		return new BenchmarkLoad(cpu, Busiest(Engine.Graphics) ?? 0, Busiest(Engine.Encoder), encoding.GetValueOrDefault(adapter)?.Count ?? 0,
			Busiest(Engine.Decoder));
	}

	private enum Engine
	{
		Graphics,
		Encoder,
		Decoder,
		Other
	}

	private static Engine KindOf(string type)
	{
		string compact = type.Replace(" ", "", StringComparison.Ordinal);
		if (compact.StartsWith("VideoEncode", StringComparison.OrdinalIgnoreCase) || compact.StartsWith("VideoCodec", StringComparison.OrdinalIgnoreCase))
			return Engine.Encoder;
		if (compact.StartsWith("VideoDecode", StringComparison.OrdinalIgnoreCase)) return Engine.Decoder;
		return compact.StartsWith("Video", StringComparison.OrdinalIgnoreCase) || compact.StartsWith("Copy", StringComparison.OrdinalIgnoreCase)
			? Engine.Other
			: Engine.Graphics;
	}

	private static long Luid(string high, string low)
	{
		return (long)(((ulong)uint.Parse(high, NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 32) |
		              uint.Parse(low, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
	}

	[GeneratedRegex(@"^pid_(?<pid>\d+)_luid_0x(?<high>[0-9A-Fa-f]+)_0x(?<low>[0-9A-Fa-f]+)_phys_\d+_eng_(?<eng>\d+)_engtype_(?<type>.*)$")]
	private static partial Regex InstancePattern();

	[StructLayout(LayoutKind.Sequential)]
	private struct FormattedValue
	{
		public uint CStatus;
		public double Value;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct CounterItem
	{
		public IntPtr Name;
		public FormattedValue Value;
	}

	[LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW")]
	private static partial uint PdhOpenQuery(IntPtr dataSource, nint userData, out IntPtr query);

	[LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
	private static partial uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, nint userData, out IntPtr counter);

	[LibraryImport("pdh.dll")]
	private static partial uint PdhCollectQueryData(IntPtr query);

	[LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
	private static partial uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, byte* itemBuffer);

	[LibraryImport("pdh.dll")]
	private static partial uint PdhCloseQuery(IntPtr query);
}
