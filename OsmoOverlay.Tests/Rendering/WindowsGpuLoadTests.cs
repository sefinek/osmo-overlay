using OsmoOverlay.Core;

namespace OsmoOverlay.Tests.Rendering;

[TestClass]
public sealed class WindowsGpuLoadTests
{
	private const int OwnPid = 100;
	private const uint Nvidia = 0x10DE;
	private const uint Amd = 0x1002;
	private const long NvidiaLuid = 0x12FBC;
	private const long AmdLuid = 0x14DF0;
	private const long SoftwareLuid = 0x14D9C;

	// As this laptop's counters name them: an RTX 4070 (VideoEncode), a Radeon 610M (Video Codec 0) and Microsoft's software adapter.
	private static readonly GpuAdapter[] Vendors =
	[
		new(0, Nvidia, NvidiaLuid, 8L << 30),
		new(1, Amd, AmdLuid, 512L << 20),
		new(2, 0x1414, SoftwareLuid, 0)
	];

	private static (string, double) Reading(int pid, long luid, int engine, string type, double percent)
	{
		return ($"pid_{pid}_luid_0x00000000_0x{luid:X8}_phys_0_eng_{engine}_engtype_{type}", percent);
	}

	[TestMethod]
	public void TheEncodersCard_IsMeasured_NotTheBusiestOne()
	{
		(string, double)[] readings =
		[
			Reading(4, AmdLuid, 0, "3D", 35),
			Reading(8, NvidiaLuid, 0, "3D", 5),
			Reading(8, NvidiaLuid, 7, "VideoEncode", 0)
		];

		BenchmarkLoad nvenc = WindowsGpuLoad.Aggregate(readings, OwnPid, Vendors, Nvidia, 0.1)!;
		Assert.AreEqual(0.05, nvenc.Gpu!.Value, 1e-9, "the integrated card drawing the desktop isn't NVENC's");
		Assert.AreEqual(0, nvenc.Encoder);
		Assert.AreEqual(0.1, nvenc.Cpu);

		Assert.AreEqual(0.35, WindowsGpuLoad.Aggregate(readings, OwnPid, Vendors, null, null)!.Gpu!.Value, 1e-9, "without an encoder, the busiest card");
	}

	[TestMethod]
	public void AnEngine_SumsItsProcesses_AndTheGpuIsItsBusiestEngine()
	{
		(string, double)[] readings =
		[
			Reading(4, NvidiaLuid, 0, "3D", 30),
			Reading(5, NvidiaLuid, 0, "3D", 25),
			Reading(6, NvidiaLuid, 3, "VideoDecode", 40)
		];

		Assert.AreEqual(0.55, WindowsGpuLoad.Aggregate(readings, OwnPid, Vendors, Nvidia, null)!.Gpu!.Value, 1e-9);
	}

	[TestMethod]
	public void ThisAppsOwnDrawing_IsLeftOut()
	{
		(string, double)[] readings = [Reading(OwnPid, NvidiaLuid, 0, "3D", 60), Reading(4, NvidiaLuid, 0, "3D", 3)];

		Assert.AreEqual(0.03, WindowsGpuLoad.Aggregate(readings, OwnPid, Vendors, Nvidia, null)!.Gpu!.Value, 1e-9);
	}

	[TestMethod]
	public void OtherProgramsEncoding_AreCountedOnTheirCard()
	{
		(string, double)[] readings =
		[
			Reading(4, NvidiaLuid, 7, "VideoEncode", 20),
			Reading(5, NvidiaLuid, 7, "VideoEncode", 10),
			Reading(6, NvidiaLuid, 7, "VideoEncode", 0.5),
			Reading(OwnPid, NvidiaLuid, 7, "VideoEncode", 30),
			Reading(7, AmdLuid, 10, "Video Codec 0", 45)
		];

		BenchmarkLoad nvenc = WindowsGpuLoad.Aggregate(readings, OwnPid, Vendors, Nvidia, null)!;
		Assert.AreEqual(0.305, nvenc.Encoder!.Value, 1e-9, "the engine sums every process on it");
		Assert.AreEqual(2, nvenc.EncoderSessions, "a process barely touching the encoder isn't encoding");

		BenchmarkLoad amf = WindowsGpuLoad.Aggregate(readings, OwnPid, Vendors, Amd, null)!;
		Assert.AreEqual(0.45, amf.Encoder!.Value, 1e-9, "AMD's encoder is its Video Codec engine");
		Assert.AreEqual(1, amf.EncoderSessions);
	}

	[TestMethod]
	public void NoCardOfTheEncodersVendor_OrNoReadings_IsUnknown()
	{
		(string, double)[] readings = [Reading(4, NvidiaLuid, 0, "3D", 10)];

		Assert.IsNull(WindowsGpuLoad.Aggregate(readings, OwnPid, Vendors, 0x8086, null));
		Assert.AreEqual(0, WindowsGpuLoad.Aggregate(readings, OwnPid, Vendors, Amd, null)!.Gpu, "a card nothing uses is idle");
		Assert.IsNull(WindowsGpuLoad.Aggregate([("not an engine", 50)], OwnPid, Vendors, null, null));
	}
}
