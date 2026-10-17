using System.Runtime.ExceptionServices;

namespace OsmoOverlay.Core;

internal static class ParallelWork
{
	/// <summary>
	///     Each item through work, at the same time (at most MaxParallel), results in the items' order. A failure comes out as
	///     the exception itself, not wrapped in an AggregateException, as it would from the sequential loop this replaces.
	/// </summary>
	public static TResult[] Map<TItem, TResult>(IReadOnlyList<TItem> items, Func<TItem, int, TResult> work)
	{
		var results = new TResult[items.Count];
		if (items.Count == 1)
		{
			results[0] = work(items[0], 0);
			return results;
		}

		try
		{
			Parallel.For(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = MaxParallel }, i => results[i] = work(items[i], i));
		}
		catch (AggregateException ex)
		{
			ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
		}

		return results;
	}

	private static int MaxParallel => Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
}
