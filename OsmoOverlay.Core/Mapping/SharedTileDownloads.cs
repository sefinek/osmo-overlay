namespace OsmoOverlay.Core.Mapping;

internal sealed class SharedTileDownloads(Func<string, CancellationToken, Task<byte[]>> download)
{
	private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
	private const long BudgetBytes = 48L * 1024 * 1024;
	private readonly Dictionary<string, Entry> _entries = new();
	private readonly Lock _gate = new();

	private sealed class Entry(CancellationTokenSource cancellation, Task<byte[]> task)
	{
		public readonly CancellationTokenSource Cancellation = cancellation;
		public readonly DateTime Added = DateTime.UtcNow;
		public readonly Task<byte[]> Download = task;
		public int Users;
	}

	public async Task<byte[]> GetAsync(string url, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		Entry entry;
		lock (_gate)
		{
			Trim();
			if (!_entries.TryGetValue(url, out entry!))
			{
				var cancellation = new CancellationTokenSource();
				entry = new Entry(cancellation, download(url, cancellation.Token));
				_entries[url] = entry;
				Entry completedEntry = entry;
				_ = entry.Download.ContinueWith(completed =>
				{
					_ = completed.Exception;
					lock (_gate) completedEntry.Cancellation.Dispose();
				}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
			}
			entry.Users++;
		}

		try
		{
			return await entry.Download.WaitAsync(ct);
		}
		finally
		{
			lock (_gate)
			{
				entry.Users--;
				if (entry.Users == 0 && !entry.Download.IsCompleted)
				{
					if (_entries.TryGetValue(url, out Entry? current) && ReferenceEquals(current, entry)) _entries.Remove(url);
					entry.Cancellation.Cancel();
				}
			}
		}
	}

	private void Trim()
	{
		DateTime expired = DateTime.UtcNow - Lifetime;
		long total = 0;
		List<KeyValuePair<string, Entry>> finished = [];
		foreach (KeyValuePair<string, Entry> pair in _entries)
		{
			if (!pair.Value.Download.IsCompleted) continue;
			if (pair.Value.Added < expired || !pair.Value.Download.IsCompletedSuccessfully)
			{
				_entries.Remove(pair.Key);
				continue;
			}
			total += pair.Value.Download.Result.Length;
			finished.Add(pair);
		}
		foreach (KeyValuePair<string, Entry> pair in finished.OrderBy(p => p.Value.Added))
		{
			if (total <= BudgetBytes) break;
			_entries.Remove(pair.Key);
			total -= pair.Value.Download.Result.Length;
		}
	}
}
