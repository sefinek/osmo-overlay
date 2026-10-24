namespace OsmoOverlay.Core.Preview;

/// <summary>How the comparison's videos are laid out: a row up to three, then as square as 16:9 pictures allow.</summary>
public static class CompareLayout
{
	public static int Columns(int count)
	{
		return count switch
		{
			<= 3 => Math.Max(1, count),
			4 => 2,
			_ => 3
		};
	}

	public static int Rows(int count)
	{
		return count <= 0 ? 0 : (count + Columns(count) - 1) / Columns(count);
	}
}
