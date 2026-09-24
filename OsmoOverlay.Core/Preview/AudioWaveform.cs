using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core.Preview;

/// <summary>
///     The audio track's peak levels for the timeline, per channel, BucketsPerSecond buckets a second - decoded once in
///     the background by an audio decoder of its own (a 25 min recording takes ~15 s; the timeline fills in as it goes).
///     Updated is raised from the background thread every few hundred milliseconds of progress.
/// </summary>
public sealed class AudioWaveform : IDisposable
{
	public const int BucketsPerSecond = 100;

	// Drawn in decibels, this far down from the recording's own loudest peak (which reaches the full height): camera
	// audio often peaks around -30 dBFS, which a linear or full-scale view draws as a thin line.
	private const double DisplayRangeDb = 48;

	// Silence (or nothing decoded yet) keeps a -60 dBFS reference, and nothing below -72 dBFS is drawn at all - otherwise
	// a near-silent recording's noise floor would fill the track.
	private const float QuietestReference = 0.001f;
	private const double DisplayFloorDb = -72;

	private readonly LibavAudioSource _source;
	private readonly float[][] _peaks;
	private readonly CancellationTokenSource _cts = new();
	private readonly Thread _worker;
	private volatile int _available;
	private volatile float _loudest;

	private AudioWaveform(LibavAudioSource source, double durationSeconds)
	{
		_source = source;
		var buckets = (int)Math.Ceiling(durationSeconds * BucketsPerSecond) + 1;
		_peaks = [.. Enumerable.Range(0, source.Channels).Select(_ => new float[buckets])];
		// A thread of its own below normal priority: ~15 s of decoding mustn't take CPU from playback and the UI.
		_worker = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Timeline waveform" };
		_worker.Start();
	}

	public int Channels => _peaks.Length;

	/// <summary>The highest peak of any channel decoded so far, 0-1.</summary>
	public float LoudestPeak => _loudest;

	/// <summary>Buckets computed so far, from the start - the rest reads as silence until then.</summary>
	public int AvailableBuckets => _available;

	public event Action? Updated;

	/// <summary>Null when the recording has no audio track. Blocking - opens the first file.</summary>
	public static AudioWaveform? Start(IReadOnlyList<PlaybackSegment> segments)
	{
		LibavAudioSource? source = LibavAudioSource.TryOpen(segments);
		return source is null ? null : new AudioWaveform(source, segments.Sum(s => s.DurationSeconds));
	}

	/// <summary>How tall to draw a peak, 0-1: decibels from DisplayRangeDb below the loudest peak (never below DisplayFloorDb) up to it.</summary>
	public static double DisplayLevel(float peak, float loudestPeak)
	{
		if (peak <= 0) return 0;

		var loudestDb = 20 * Math.Log10(Math.Max(loudestPeak, QuietestReference));
		var floorDb = Math.Max(loudestDb - DisplayRangeDb, DisplayFloorDb);
		return Math.Clamp((20 * Math.Log10(peak) - floorDb) / (loudestDb - floorDb), 0, 1);
	}

	/// <summary>0-1 peak of a channel over buckets [from, to).</summary>
	public float Peak(int channel, int from, int to)
	{
		float[] peaks = _peaks[channel];
		to = Math.Min(to, Math.Min(_available, peaks.Length));
		var peak = 0f;
		for (var i = Math.Max(0, from); i < to; i++)
			if (peaks[i] > peak)
				peak = peaks[i];

		return peak;
	}

	private void Run()
	{
		CancellationToken ct = _cts.Token;
		try
		{
			var channels = _source.Channels;
			var samplesPerBucket = (double)_source.SampleRate / BucketsPerSecond;
			long sampleFrame = 0;
			var lastUpdate = Environment.TickCount64;

			_source.Seek(0);
			while (!ct.IsCancellationRequested)
			{
				ReadOnlySpan<float> samples = _source.Read(double.MaxValue);
				if (samples.IsEmpty) break;

				for (var i = 0; i < samples.Length; i += channels, sampleFrame++)
				{
					var bucket = (int)(sampleFrame / samplesPerBucket);
					if (bucket >= _peaks[0].Length) break;

					for (var c = 0; c < channels; c++)
					{
						var level = Math.Abs(samples[i + c]);
						if (level > _peaks[c][bucket]) _peaks[c][bucket] = level;
						if (level > _loudest) _loudest = level;
					}
				}

				_available = (int)(sampleFrame / samplesPerBucket);
				if (Environment.TickCount64 - lastUpdate < 250) continue;

				lastUpdate = Environment.TickCount64;
				Updated?.Invoke();
			}

			_available = _peaks[0].Length;
			Updated?.Invoke();
		}
		catch (InvalidOperationException ex)
		{
			if (!ct.IsCancellationRequested) AppLogger.Warn(ex, "Timeline waveform stopped");
		}
	}

	public void Dispose()
	{
		_cts.Cancel();
		_worker.Join(TimeSpan.FromSeconds(2));
		_source.Dispose();
		_cts.Dispose();
	}
}
