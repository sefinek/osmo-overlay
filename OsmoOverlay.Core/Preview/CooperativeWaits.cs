using System.Threading.Channels;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     Waits that end quietly when their token is cancelled: they return false instead of throwing. Stopping a playback
///     or the thumbnail worker cancels several waiting stages at once, and a thrown OperationCanceledException each is
///     slow and floods a debugger's first-chance log - the cancellation is the expected way these loops end.
/// </summary>
internal static class CooperativeWaits
{
	/// <summary>True when there is something to read; false when the channel is completed and empty, or the token fired.</summary>
	public static ValueTask<bool> WaitToReadQuietlyAsync<T>(this ChannelReader<T> reader, CancellationToken ct)
	{
		if (ct.IsCancellationRequested) return ValueTask.FromResult(false);
		return reader.TryPeek(out _) ? ValueTask.FromResult(true) : Quietly(reader.WaitToReadAsync(ct).AsTask());
	}

	/// <summary>True when there is room to write; false when the channel is completed, or the token fired.</summary>
	public static ValueTask<bool> WaitToWriteQuietlyAsync<T>(this ChannelWriter<T> writer, CancellationToken ct)
	{
		return Quietly(writer.WaitToWriteAsync(ct).AsTask());
	}

	/// <summary>True when the semaphore was taken; false when the token fired first.</summary>
	public static ValueTask<bool> WaitQuietlyAsync(this SemaphoreSlim semaphore, CancellationToken ct)
	{
		return Quietly(semaphore.WaitAsync(Timeout.Infinite, ct));
	}

	/// <summary>True when the time passed; false when the token fired first.</summary>
	public static async ValueTask<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
	{
		await Task.Delay(delay, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
		return !ct.IsCancellationRequested;
	}

	// WhenAny never throws for the task it's given - a cancelled wait just reads as false, while a real failure
	// (a channel completed with an error) still surfaces from the await below.
	private static async ValueTask<bool> Quietly(Task<bool> wait)
	{
		if (!wait.IsCompleted) await Task.WhenAny(wait);
		return !wait.IsCanceled && await wait;
	}
}
