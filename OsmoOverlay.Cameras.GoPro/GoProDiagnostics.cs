namespace OsmoOverlay.Cameras.GoPro;

internal sealed class GoProDiagnostics
{
	private readonly List<(double Start, double End, GpmfStream Stream)> _blocks = [];
	private int _block;

	public GoProDiagnostics(IReadOnlyList<GpmfPacket> packets, string key, int axes, double? clockStart)
	{
		foreach (GpmfPacket packet in packets)
		foreach (GpmfStream stream in packet.Devices.SelectMany(d => d.Streams).Where(s => s.Key == key && s.Axes == axes && s.Count > 0))
		{
			double start = clockStart is { } clock && stream.StampMicroseconds is { } stamp ? (stamp - clock) / 1e6 : packet.StartSeconds;
			_blocks.Add((start, start + packet.DurationSeconds, stream));
		}
		_blocks.Sort((a, b) => a.Start.CompareTo(b.Start));
		for (int i = 0; i + 1 < _blocks.Count; i++)
		{
			var block = _blocks[i];
			double duration = block.End - block.Start;
			double measured = _blocks[i + 1].Start - block.Start;
			// Keep sensor timing between adjacent blocks without stretching samples across a missing packet.
			if (measured > 0.5 * duration && measured < 1.5 * duration)
				_blocks[i] = (block.Start, _blocks[i + 1].Start, block.Stream);
		}
	}

	public double? At(double seconds, int axis)
	{
		if (_blocks.Count == 0) return null;
		while (_block + 1 < _blocks.Count && _blocks[_block + 1].Start <= seconds) _block++;
		(double start, double end, GpmfStream stream) = _blocks[_block];
		if (seconds < start || seconds >= end) return null;

		int sample = Math.Min(stream.Count - 1, (int)((seconds - start) / (end - start) * stream.Count));
		double value = stream.Values[sample * stream.Axes + axis];
		return double.IsFinite(value) ? value : null;
	}
}
