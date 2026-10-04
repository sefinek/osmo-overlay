using OsmoOverlay.Core;

namespace OsmoOverlay.Tests.Rendering;

[TestClass]
public sealed class HardwareCheckTests
{
	private static HardwareReport Report(int threads = 32, long memoryBytes = 32L << 30, string[]? gpus = null, string? encoder = "hevc_nvenc", bool? decode = true,
		long? freeGb = 500, bool is64Bit = true)
	{
		return new HardwareReport(new BenchmarkSystem("Windows", "CPU", threads, memoryBytes, gpus ?? ["RTX"], "9.0", "1.0.0"), is64Bit, encoder, null, decode,
			"d3d11va", "C:\\Videos", freeGb is { } gb ? gb << 30 : null);
	}

	[TestMethod]
	public void AStrongLaptop_IsOk()
	{
		Assert.AreEqual(CheckStatus.Ok, Report().Overall);
	}

	[TestMethod]
	[DataRow(4.0, CheckStatus.Problem)]
	[DataRow(7.4, CheckStatus.Problem)]
	[DataRow(8.0, CheckStatus.Warning)]
	[DataRow(7.8, CheckStatus.Warning, DisplayName = "8 GB as Linux reads it")]
	[DataRow(15.6, CheckStatus.Ok, DisplayName = "16 GB as Linux reads it")]
	[DataRow(16.0, CheckStatus.Ok)]
	public void Memory(double gb, CheckStatus expected)
	{
		Assert.AreEqual(expected, Report(memoryBytes: (long)(gb * (1L << 30))).MemoryStatus);
	}

	[TestMethod]
	[DataRow(2, CheckStatus.Problem)]
	[DataRow(6, CheckStatus.Warning)]
	[DataRow(8, CheckStatus.Ok)]
	public void Processor(int threads, CheckStatus expected)
	{
		Assert.AreEqual(expected, Report(threads).ProcessorStatus);
	}

	[TestMethod]
	public void Encoder_OnTheCpu_IsAWarning_WithoutFfmpeg_Unknown()
	{
		Assert.AreEqual(CheckStatus.Warning, Report(encoder: "libx265").EncoderStatus);
		Assert.AreEqual(CheckStatus.Unknown, Report(encoder: null).EncoderStatus);
		Assert.AreEqual(CheckStatus.Warning, Report(decode: false).DecoderStatus);
		Assert.AreEqual(CheckStatus.Unknown, Report(decode: null).DecoderStatus);
	}

	[TestMethod]
	public void Disk_AsksForTwoHoursOf4K()
	{
		Assert.AreEqual(CheckStatus.Problem, Report(freeGb: 10).DiskStatus);
		Assert.AreEqual(CheckStatus.Warning, Report(freeGb: 50).DiskStatus, "an hour of 4K takes ~66 GB while it's rendered");
		Assert.AreEqual(CheckStatus.Ok, Report(freeGb: 100).DiskStatus);
		Assert.AreEqual(CheckStatus.Unknown, Report(freeGb: null).DiskStatus);
	}

	[TestMethod]
	public void Overall_IsTheWorst_LeavingOutWhatCouldntBeChecked()
	{
		Assert.AreEqual(CheckStatus.Problem, Report(memoryBytes: 4L << 30, encoder: "libx265").Overall);
		Assert.AreEqual(CheckStatus.Ok, Report(encoder: null, decode: null, gpus: []).Overall);
		Assert.AreEqual(CheckStatus.Problem, Report(is64Bit: false).Overall);
	}
}
