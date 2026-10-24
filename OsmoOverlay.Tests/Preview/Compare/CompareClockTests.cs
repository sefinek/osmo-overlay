using System.Diagnostics;
using OsmoOverlay.Core.Preview;

namespace OsmoOverlay.Tests.Preview;

[TestClass]
public sealed class CompareClockTests
{
	private long _ticks;

	private CompareClock Clock()
	{
		return new CompareClock(() => _ticks);
	}

	private void Advance(double seconds)
	{
		_ticks += (long)(seconds * Stopwatch.Frequency);
	}

	[TestMethod]
	public void Paused_StandsStill()
	{
		CompareClock clock = Clock();
		clock.Seek(5);

		Advance(3);

		Assert.AreEqual(5, clock.Position, 1e-9);
		Assert.IsFalse(clock.IsRunning);
	}

	[TestMethod]
	public void Started_RunsFromWhereItWasStarted()
	{
		CompareClock clock = Clock();
		clock.Start(10);

		Advance(2.5);

		Assert.AreEqual(12.5, clock.Position, 1e-6);
	}

	[TestMethod]
	public void Pause_KeepsThePositionItReached()
	{
		CompareClock clock = Clock();
		clock.Start(1);
		Advance(2);

		clock.Pause();
		Advance(10);

		Assert.AreEqual(3, clock.Position, 1e-6);
	}

	[TestMethod]
	public void ChangingTheRate_ScalesOnlyFromThenOn()
	{
		CompareClock clock = Clock();
		clock.Start(0);
		Advance(2);

		clock.Rate = 2;
		Advance(3);

		Assert.AreEqual(8, clock.Position, 1e-6);
	}

	[TestMethod]
	public void Seek_MovesARunningClockAndKeepsItRunning()
	{
		CompareClock clock = Clock();
		clock.Start(0);
		Advance(5);

		clock.Seek(20);
		Advance(1);

		Assert.AreEqual(21, clock.Position, 1e-6);
		Assert.IsTrue(clock.IsRunning);
	}
}
