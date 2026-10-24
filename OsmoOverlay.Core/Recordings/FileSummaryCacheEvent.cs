namespace OsmoOverlay.Core;

public enum FileSummaryCacheEventKind
{
	/// <summary>Valid entry found - nothing gets recomputed.</summary>
	Hit,
	/// <summary>No usable entry (never analyzed, file changed since, or unreadable) - recomputing.</summary>
	Miss,
	/// <summary>Entry for this exact file, but written by an older FormatVersion - deleted, recomputing.</summary>
	Stale,
	/// <summary>The recomputed summary was written to the cache.</summary>
	Saved,
	/// <summary>The recomputed summary couldn't be written (details in the log) - the next run recomputes again.</summary>
	SaveFailed
}

/// <summary>PreviousFormatVersion and PreviousSizeBytes are set only for Stale - the version the deleted entry was written with and its file size.</summary>
public sealed record FileSummaryCacheEvent(FileSummaryCacheEventKind Kind, int CurrentFormatVersion, int? PreviousFormatVersion = null, long PreviousSizeBytes = 0);
