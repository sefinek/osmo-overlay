namespace Telemavix.Core.Preview;

/// <summary>Sound the preview pushes to the playback device a chunk at a time: a recording's own (LibavAudioSource) or silence (SilentAudioSource).</summary>
internal interface IAudioSamples : IDisposable
{
	int SampleRate { get; }
	int Channels { get; }

	void Seek(double seconds);

	/// <summary>The next samples (interleaved, Channels per sample frame) that start before endSeconds, cut off at it; empty at the end. Valid until the next call.</summary>
	ReadOnlySpan<float> Read(double endSeconds);
}
