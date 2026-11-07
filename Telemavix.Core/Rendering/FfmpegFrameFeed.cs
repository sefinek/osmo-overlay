using System.Diagnostics;
using System.Threading.Channels;

namespace Telemavix.Core;

/// <param name="Cancelled">The render was cancelled; ffmpeg is dead and `ExitCode` and `StandardError` mean nothing.</param>
/// <param name="FramesWritten">Frames written to ffmpeg's input.</param>
internal sealed record FrameFeedResult(bool Cancelled, int ExitCode, string StandardError, int FramesWritten);

/// <summary>
///     Feeds raw frames to a running ffmpeg's stdin and waits for it to end - what every render that draws its own frames has in
///     common (RenderJob, TrackRenderJob). Drawing (CPU) and writing (I/O) overlap on two threads. The caller still owns the
///     process: it must leave it dead and remove an incomplete output on every path out (Disposing a Process doesn't end it).
/// </summary>
internal static class FfmpegFrameFeed
{
	/// <param name="produce">
	///     Draws frames on its own thread and writes each to the channel; OperationCanceledException ends it quietly, any other
	///     exception reaches the reader. It is the only code that touches the renderer.
	/// </param>
	/// <param name="written">Called on the writing side after each frame went to ffmpeg, with the buffer (to recycle) and how many are written.</param>
	public static async Task<FrameFeedResult> RunAsync(Process ffmpeg, int prefetchFrames, Func<ChannelWriter<byte[]>, CancellationToken, Task> produce,
		Action<byte[], int> written, CancellationToken ct)
	{
		Stream stdin = ffmpeg.StandardInput.BaseStream;
		Task<string> stderrTask = ffmpeg.StandardError.ReadToEndAsync(ct);

		// stdin.Write can't be cancelled; killing ffmpeg breaks the pipe and unblocks it as an IOException (caught below).
		using CancellationTokenRegistration killOnCancel = ct.Register(() => RenderJob.KillFfmpegIfRunning(ffmpeg));

		var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(prefetchFrames) { SingleReader = true, SingleWriter = true });

		// Linked so the reader's finally can always unblock the producer, even when the reader stopped for a reason
		// other than `ct` (ffmpeg crashing) and nobody drains the channel any more.
		using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		CancellationToken producerCt = producerCts.Token;

		var producer = Task.Run(async () =>
		{
			try
			{
				await produce(channel.Writer, producerCt);
				channel.Writer.TryComplete();
			}
			catch (OperationCanceledException)
			{
				channel.Writer.TryComplete();
			}
			catch (Exception ex)
			{
				channel.Writer.TryComplete(ex);
			}
		}, producerCt);

		int count = 0;
		try
		{
			await foreach (byte[] pixels in channel.Reader.ReadAllAsync(ct))
			{
				stdin.Write(pixels, 0, pixels.Length);
				written(pixels, ++count);
			}

			stdin.Flush();
		}
		catch (OperationCanceledException)
		{
			// The cancelled check below decides whether to report it - the producer may also stop first and end the channel normally.
		}
		catch (IOException)
		{
			// ffmpeg's pipe broke - either it died on its own, or killOnCancel above just killed it
			// because of a cancellation already in flight; the checks below sort out which.
		}
		finally
		{
			producerCts.Cancel();
			stdin.Close();

			try
			{
				await producer;
			}
			catch
			{
				// Any real failure was already observed via the channel (ReadAllAsync rethrows it
				// above) - a second throw here would only be a duplicate.
			}
		}

		if (ct.IsCancellationRequested)
		{
			// killOnCancel already killed ffmpeg - closing stdin alone wouldn't stop it: the overlay's eof_action
			// repeats the last frame against the rest of the main input (endless for a green screen). Not ct here,
			// it's cancelled and this wait must finish.
			await ffmpeg.WaitForExitAsync(CancellationToken.None);
			try
			{
				await stderrTask;
			}
			catch (Exception)
			{
				// A killed process's stderr can fault (broken pipe, the token) - it's a cancellation either way.
			}

			return new FrameFeedResult(true, 0, "", count);
		}

		await ffmpeg.WaitForExitAsync(CancellationToken.None);
		return new FrameFeedResult(false, ffmpeg.ExitCode, await stderrTask, count);
	}
}
