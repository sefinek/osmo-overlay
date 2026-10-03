using System.Runtime.InteropServices;

namespace OsmoOverlay.Core;

/// <summary>A graphics adapter as DXGI lists it: its place in the list (what ffmpeg's d3d11va device takes), PCI vendor, LUID, own memory and name.</summary>
internal sealed record GpuAdapter(int Index, uint VendorId, long Luid, ulong DedicatedMemoryBytes, string Name = "");

/// <summary>
///     The graphics adapters on Windows, from DXGI - empty elsewhere or when it can't be asked. Read once: what an encoder runs
///     on and what the load is measured on must be the same adapter for the app's whole run.
/// </summary>
internal static unsafe partial class GpuAdapters
{
	private static readonly Guid DxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
	private static readonly Lazy<IReadOnlyList<GpuAdapter>> Listed = new(Enumerate);

	public static IReadOnlyList<GpuAdapter> All => Listed.Value;

	/// <summary>
	///     The adapter `vendor` makes that an encoder should use: the one with the most memory of its own - a laptop's dedicated
	///     card rather than the integrated one of the same vendor (Radeon RX next to a Ryzen's Radeon, Arc next to Intel's
	///     integrated graphics), which DXGI usually lists first. Null when there's none (or no DXGI).
	/// </summary>
	public static GpuAdapter? Pick(IEnumerable<GpuAdapter> adapters, uint vendor)
	{
		return adapters.Where(a => a.VendorId == vendor).OrderByDescending(a => a.DedicatedMemoryBytes).ThenBy(a => a.Index).FirstOrDefault();
	}

	private static IReadOnlyList<GpuAdapter> Enumerate()
	{
		if (!OperatingSystem.IsWindows()) return [];

		List<GpuAdapter> adapters = [];
		IntPtr factory;
		Guid iid = DxgiFactory1;
		try
		{
			if (CreateDXGIFactory1(&iid, &factory) < 0) return adapters;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return adapters;
		}

		try
		{
			// IDXGIFactory1::EnumAdapters1 and IDXGIAdapter1::GetDesc1, by their places in the interfaces' tables.
			var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)factory)[12];
			for (uint i = 0;; i++)
			{
				IntPtr adapter;
				if (enumAdapters1(factory, i, &adapter) < 0) break;

				try
				{
					var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)(*(void***)adapter)[10];
					AdapterDesc1 desc;
					if (getDesc1(adapter, &desc) >= 0)
						adapters.Add(new GpuAdapter((int)i, desc.VendorId, ((long)desc.LuidHighPart << 32) | desc.LuidLowPart, desc.DedicatedVideoMemory,
							new string(desc.Description).TrimEnd('\0').Trim()));
				}
				finally
				{
					Marshal.Release(adapter);
				}
			}
		}
		finally
		{
			Marshal.Release(factory);
		}

		return adapters;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct AdapterDesc1
	{
		public fixed char Description[128];
		public uint VendorId;
		public uint DeviceId;
		public uint SubSysId;
		public uint Revision;
		public nuint DedicatedVideoMemory;
		public nuint DedicatedSystemMemory;
		public nuint SharedSystemMemory;
		public uint LuidLowPart;
		public int LuidHighPart;
		public uint Flags;
	}

	[LibraryImport("dxgi.dll")]
	private static partial int CreateDXGIFactory1(Guid* riid, IntPtr* factory);
}
