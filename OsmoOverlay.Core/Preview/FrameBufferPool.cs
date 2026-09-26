using System.Collections.Concurrent;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     Reuses full-frame BGRA buffers across preview frames - each one is megabytes (large object heap), and
///     allocating two per frame at 60 fps kept the GC doing full collections, showing up as playback hitches.
///     Not ArrayPool.Shared: it hands out arrays longer than requested, and every consumer here relies on
///     Length being exactly one frame. Thread-safe: rented on the decode thread, returned on the UI thread.
/// </summary>
internal sealed class FrameBufferPool(int maxRetained)
{
	private readonly ConcurrentQueue<byte[]> _free = new();

	public byte[] Rent(int size)
	{
		// A buffer of another size is left over from a previous preview resolution - drop it.
		while (_free.TryDequeue(out var buffer))
			if (buffer.Length == size)
				return buffer;

		return new byte[size];
	}

	public void Return(byte[] buffer)
	{
		if (_free.Count < maxRetained) _free.Enqueue(buffer);
	}
}
