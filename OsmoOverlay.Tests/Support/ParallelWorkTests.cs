using OsmoOverlay.Core;

namespace OsmoOverlay.Tests.Support;

[TestClass]
public sealed class ParallelWorkTests
{
	[TestMethod]
	public void Map_KeepsTheItemsOrder_WhateverFinishesFirst()
	{
		int[] items = [.. Enumerable.Range(0, 12)];

		int[] results = ParallelWork.Map(items, (item, _) =>
		{
			Thread.Sleep((12 - item) * 5);
			return item * 10;
		});

		CollectionAssert.AreEqual(items.Select(i => i * 10).ToArray(), results);
	}

	[TestMethod]
	public void Map_PassesTheIndex()
	{
		string[] results = ParallelWork.Map(["a", "b", "c"], (item, i) => $"{i}{item}");

		CollectionAssert.AreEqual(new[] { "0a", "1b", "2c" }, results);
	}

	[TestMethod]
	public void Map_SingleItem_RunsOnTheCallingThread()
	{
		int caller = Environment.CurrentManagedThreadId;

		int[] results = ParallelWork.Map([1], (_, _) => Environment.CurrentManagedThreadId);

		Assert.AreEqual(caller, results[0]);
	}

	[TestMethod]
	public void Map_Failure_ComesOutAsTheExceptionItself()
	{
		Assert.ThrowsExactly<InvalidDataException>(() =>
			ParallelWork.Map([1, 2, 3], (item, _) => item == 2 ? throw new InvalidDataException("bad file") : item));
	}

	[TestMethod]
	public void Map_RunsTheItemsAtTheSameTime()
	{
		using var both = new CountdownEvent(2);

		bool[] results = ParallelWork.Map([0, 1], (_, _) =>
		{
			both.Signal();
			return both.Wait(TimeSpan.FromSeconds(5));
		});

		Assert.IsTrue(results.All(r => r), "each waited for the other, so they did not run one after another");
	}
}
