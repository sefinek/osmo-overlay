using OsmoOverlay.Core.Overlay;
using OsmoOverlay.Core.Telemetry;

namespace OsmoOverlay.Tests.Overlay;

[TestClass]
public sealed class GMeterDeltasTests
{
	private static List<DerivedFrame> Frames(Func<int, double> lateral, int count = 100, Func<int, bool>? startsAfterCut = null)
	{
		var frames = new List<DerivedFrame>();
		for (int i = 0; i < count; i++)
		{
			var raw = new TelemetryFrame(i, i * 0.1, 0, 0, 0, null, 0, 0, 0);
			frames.Add(new DerivedFrame(raw, 0, 0, 0, 0, 0, 0, new SunPosition(0, 0), 0, 0, 1, lateral(i), 0, startsAfterCut?.Invoke(i) ?? false));
		}

		return frames;
	}

	[TestMethod]
	public void SteadyReading_IsTheBaseline()
	{
		(double Lateral, double Longitudinal)[] deltas = GMeterDeltas.Compute(Frames(_ => 0.4));

		Assert.IsTrue(deltas.All(d => Math.Abs(d.Lateral) < 1e-9));
	}

	[TestMethod]
	public void SuddenForce_ShowsAndFadesIntoTheBaseline()
	{
		(double Lateral, double Longitudinal)[] deltas = GMeterDeltas.Compute(Frames(i => i < 50 ? 0 : 1.0));

		Assert.IsTrue(deltas[55].Lateral > 0.3);
		Assert.IsTrue(deltas[99].Lateral < deltas[55].Lateral);
	}

	[TestMethod]
	public void EveryFrameKeepsItsValue_WhateverWasDrawnBefore()
	{
		List<DerivedFrame> frames = Frames(i => i < 50 ? 0 : 1.0);
		(double Lateral, double Longitudinal)[] deltas = GMeterDeltas.Compute(frames);

		Assert.IsTrue(deltas[60].Lateral > 0);
		Assert.AreEqual(deltas[60], GMeterDeltas.Compute(frames)[60]);
	}

	[TestMethod]
	public void AfterACut_StartsOver()
	{
		(double Lateral, double Longitudinal)[] deltas = GMeterDeltas.Compute(Frames(i => i < 50 ? 0 : 1.0, startsAfterCut: i => i == 50));

		Assert.AreEqual(0, deltas[50].Lateral, 1e-9);
	}
}
