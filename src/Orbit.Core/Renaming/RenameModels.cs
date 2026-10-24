using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Orbit.RenameSmoke")]

namespace Orbit.Core.Renaming;

public sealed record RenameOptions
{
    public string Prefix {get;init;}="";
    public string Suffix {get;init;}="";
    public string Search {get;init;}="";
    public string Replacement {get;init;}="";
    public string? NumberedBaseName {get;init;}
    public int StartNumber {get;init;}=1;
    public int Digits {get;init;}=3;
    public bool KeepExtension=>true;
}

public sealed record RenamePlanItem(string OriginalPath,string TargetPath,long Length,DateTime LastWriteTimeUtc)
{
    public bool HasChange=>!string.Equals(OriginalPath,TargetPath,StringComparison.Ordinal);
}

public sealed class RenamePlan
{
    internal RenamePlan(IReadOnlyList<RenamePlanItem> items,IReadOnlyList<RenameStamp> stamps)
    {Items=items;Stamps=stamps;TotalBytes=items.Sum(item=>item.Length);}
    public IReadOnlyList<RenamePlanItem> Items {get;}
    public long TotalBytes {get;}
    public bool HasChanges=>Items.Any(item=>item.HasChange);
    internal IReadOnlyList<RenameStamp> Stamps {get;}
}

public sealed record RenameFingerprint(long Length,DateTime LastWriteTimeUtc,string? FileIdentity,string Sha256);
public sealed record RenameReceiptItem(string OriginalPath,string CurrentPath,RenameFingerprint Fingerprint);

public sealed class RenameReceipt
{
    private int _undoState;
    internal RenameReceipt(IReadOnlyList<RenameReceiptItem> items){Items=items;}
    public IReadOnlyList<RenameReceiptItem> Items {get;}
    public bool IsUndone=>Volatile.Read(ref _undoState)==2;
    internal bool BeginUndo()=>Interlocked.CompareExchange(ref _undoState,1,0)==0;
    internal void FinishUndo(bool success)=>Volatile.Write(ref _undoState,success?2:0);
}

public sealed record RenameLocation(string OriginalPath,string CurrentPath,bool ExpectedFilePresent,string? Problem);

public sealed class RenameException : Exception
{
    public RenameException(string message,Exception? innerException=null,IReadOnlyList<RenameLocation>? remainingItems=null):base(message,innerException)
    {RemainingItems=remainingItems??Array.Empty<RenameLocation>();}
    public IReadOnlyList<RenameLocation> RemainingItems {get;}
    public bool RollbackFailed=>RemainingItems.Count>0;
}

internal sealed record RenameStamp(long Length,DateTime LastWriteTimeUtc,string? FileIdentity);
