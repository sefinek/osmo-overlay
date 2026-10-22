namespace OsmoOverlay.Cameras.GoPro;

/// <summary>A run of GPMF blocks of one stream - `Count` samples of `Axes` numbers, at `Times` seconds on the video's clock.</summary>
internal sealed class GpmfSeries
{
	private GpmfSeries(double[] times, double[] values, int axes, List<GpmfSpan> spans)
	{
		Times = times;
		Values = values;
		Axes = axes;
		Spans = spans;
	}

	public double[] Times { get; }
	public double[] Values { get; }
	public int Axes { get; }

	/// <summary>The blocks it was built from, with the index of the first sample of each.</summary>
	public List<GpmfSpan> Spans { get; }

	public int Count => Times.Length;

	/// <summary>
	///     The key's samples from every packet. A block's start is its STMP less `clockStart` (the sensors' clock at the video's
	///     first frame), the packet's own start without either; its samples are as far apart as the next block's start says,
	///     the packet's length shared out evenly where that doesn't (the last block, a missing or backwards clock).
	/// </summary>
	public static GpmfSeries Build(IReadOnlyList<GpmfPacket> packets, string key, double? clockStart)
	{
		List<(GpmfStream Stream, double Start, double PacketStep)> blocks = [];
		int axes = 0;
		foreach (GpmfPacket packet in packets)
		{
			foreach (GpmfDevice device in packet.Devices)
			{
				foreach (GpmfStream stream in device.Streams.Where(s => s.Key == key && s.Count > 0))
				{
					if (axes == 0) axes = stream.Axes;
					if (stream.Axes != axes) continue;

					double start = clockStart is { } clock && stream.StampMicroseconds is { } stamp ? (stamp - clock) / 1e6 : packet.StartSeconds;
					blocks.Add((stream, start, packet.DurationSeconds / stream.Count));
				}
			}
		}

		List<double> times = [];
		List<double> values = [];
		List<GpmfSpan> spans = [];
		double previousStep = 0;
		double latest = double.NegativeInfinity;
		for (int b = 0; b < blocks.Count; b++)
		{
			(GpmfStream stream, double start, double packetStep) = blocks[b];
			double step = packetStep;
			if (b + 1 < blocks.Count)
			{
				double measured = (blocks[b + 1].Start - start) / stream.Count;
				if (measured > 0 && measured < 4 * packetStep) step = measured;
			}
			else if (previousStep > 0)
			{
				step = previousStep;
			}

			previousStep = step;

			spans.Add(new GpmfSpan(stream, times.Count, start, step));
			for (int k = 0; k < stream.Count; k++)
			{
				latest = Math.Max(latest, start + k * step);
				times.Add(latest);
			}

			values.AddRange(stream.Values);
		}

		return new GpmfSeries([.. times], [.. values], axes, spans);
	}

	/// <summary>
	///     The value at each video frame's time (frame / fps): the mean of the samples within half a frame of it when `average`
	///     (a sensor much faster than the video), else - and where no sample falls there - the nearest one.
	/// </summary>
	public double[] Resample(int frames, double fps, bool average)
	{
		double[] result = new double[frames * Axes];
		double half = 0.5 / fps;
		for (int f = 0; f < frames; f++)
		{
			double t = f / fps;
			int from = average ? LowerBound(t - half) : 0;
			int to = average ? LowerBound(t + half) : 0;
			if (to > from)
			{
				for (int i = from; i < to; i++)
				{
					for (int axis = 0; axis < Axes; axis++) result[f * Axes + axis] += Values[i * Axes + axis] / (to - from);
				}
			}
			else
			{
				Array.Copy(Values, Nearest(t) * Axes, result, f * Axes, Axes);
			}
		}

		return result;
	}

	/// <summary>The index of the sample nearest in time - Count must be above 0.</summary>
	public int Nearest(double t)
	{
		int i = LowerBound(t);
		if (i == Count) return Count - 1;

		return i > 0 && t - Times[i - 1] <= Times[i] - t ? i - 1 : i;
	}

	private int LowerBound(double t)
	{
		int index = Array.BinarySearch(Times, t);
		if (index < 0) return ~index;

		while (index > 0 && Times[index - 1] >= t) index--;
		return index;
	}
}

/// <summary>One GPMF block in a GpmfSeries: its first sample's index, time and the seconds between its samples.</summary>
internal readonly record struct GpmfSpan(GpmfStream Stream, int FirstSample, double StartSeconds, double StepSeconds);
