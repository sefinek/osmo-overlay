using System.Runtime.InteropServices;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     Requests Windows' 1ms system timer resolution for the lifetime of this instance, restoring
///     whatever it was when disposed - Task.Delay (used by RunPlaybackAsync to pace real-time video
///     playback) inherits the OS-wide timer resolution, and Windows' own default (~15.6ms) is coarse
///     enough that a short delay (a handful of ms, as needed between 59.94fps frames) can overshoot by
///     nearly a whole frame interval - measured average ~5.8ms of pure timer overshoot per frame on a
///     4K60 source, on top of the actual decode/compose work. Scoped to just the active playback loop
///     (not the whole app) since raising this system-wide has a small but real power cost while
///     active - not worth paying outside of something that actually needs sub-frame timing precision.
///     No-op on non-Windows platforms (nothing else on this codebase's supported platforms needs it -
///     scrubbing/seeking never uses Task.Delay in the first place).
/// </summary>
internal sealed class HighResolutionTimer : IDisposable
{
	private const uint ResolutionMs = 1;
	private const uint TimerrNoError = 0;

	private readonly bool _active;

	public HighResolutionTimer()
	{
		if (!OperatingSystem.IsWindows()) return;

		try
		{
			_active = TimeBeginPeriod(ResolutionMs) == TimerrNoError;
		}
		catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
		{
		}
	}

	public void Dispose()
	{
		if (!_active) return;

		try
		{
			TimeEndPeriod(ResolutionMs);
		}
		catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
		{
		}
	}

	[DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
	private static extern uint TimeBeginPeriod(uint uMilliseconds);

	[DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
	private static extern uint TimeEndPeriod(uint uMilliseconds);
}
