using System.Globalization;

namespace Orbit.Core.Renaming;

/// <summary>Same-directory, non-overwriting file renames with a preview snapshot and guarded in-memory undo.</summary>
public sealed class RenameEngine
{
    public const int MaximumFiles=1000;
    public const long MaximumTotalBytes=2L*1024*1024*1024;
    private readonly SemaphoreSlim _mutationGate=new(1,1);
    // Deterministic fault/cancellation tests use real files through an internal friend-assembly seam.
    internal Action<string,string,string>? BeforeMove {get;set;}

    public Task<RenamePlan> CreatePlanAsync(IReadOnlyList<string> paths,RenameOptions options,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(paths);ArgumentNullException.ThrowIfNull(options);
        var snapshot=paths.ToArray();
        return Task.Run(()=>CreatePlan(snapshot,options,cancellationToken),cancellationToken);
    }
    public Task<RenameReceipt> ApplyAsync(RenamePlan plan,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.Run(async()=>{
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try{return await ApplyCoreAsync(plan,cancellationToken).ConfigureAwait(false);}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_B37A394D2A7A")+ex.Message,ex);}
            finally{_mutationGate.Release();}
        },cancellationToken);
    }
    public Task UndoAsync(RenameReceipt receipt,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return Task.Run(async()=>{
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try {
                if(!receipt.BeginUndo())throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_46C442BB349D"));
                bool success=false;var leases=new List<RenameLease>();
                try {
                    var routes=receipt.Items.Select(item=>(Source:item.CurrentPath,Target:item.OriginalPath)).ToArray();
                    RenameFiles.CheckTargets(routes,cancellationToken);
                    var changes=new List<Change>();
                    foreach(var item in receipt.Items) {
                        cancellationToken.ThrowIfCancellationRequested();
                        var lease=await RenameFiles.LockFingerprintAsync(item.CurrentPath,cancellationToken).ConfigureAwait(false);leases.Add(lease);var actual=lease.Fingerprint;
                        if(!RenameFiles.SameFingerprint(actual,item.Fingerprint))throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_33D7A7269412")+item.CurrentPath);
                        changes.Add(new(item.CurrentPath,item.OriginalPath,actual));
                    }
                    Execute(changes,cancellationToken);
                    success=true;
                }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_D37D44E02573")+ex.Message,ex);}
                finally{foreach(var lease in leases)lease.Dispose();receipt.FinishUndo(success);}
            }finally{_mutationGate.Release();}
        },cancellationToken);
    }

    private static RenamePlan CreatePlan(IReadOnlyList<string> paths,RenameOptions options,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(paths.Count is <1 or >MaximumFiles)throw new RenameException(global::Orbit.Core.Localization.L.Format("Text_3122227F7DA4",(object?)(MaximumFiles)));
        if(options.StartNumber<0||options.Digits is <1 or >9)throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_EA58E27EDD6E"));
        var items=new List<RenamePlanItem>();var stamps=new List<RenameStamp>();var seen=new HashSet<string>(RenameFiles.PathComparer);long total=0;
        try {
            for(int i=0;i<paths.Count;i++) {
                ct.ThrowIfCancellationRequested();var source=RenameFiles.FullPath(paths[i]);
                if(!seen.Add(source))throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_14DAD0F24746")+source);
                var stamp=RenameFiles.ReadStamp(source);
                if((total=checked(total+stamp.Length))>MaximumTotalBytes)throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_12BBA0E7DB3D"));
                var extension=Path.GetExtension(source);var stem=Path.GetFileNameWithoutExtension(source);
                if(!string.IsNullOrEmpty(options.NumberedBaseName))stem=options.NumberedBaseName+checked(options.StartNumber+i).ToString("D"+options.Digits,CultureInfo.InvariantCulture);
                else if(!string.IsNullOrEmpty(options.Search))stem=stem.Replace(options.Search,options.Replacement??"",StringComparison.Ordinal);
                var leaf=(options.Prefix??"")+stem+(options.Suffix??"")+extension;
                RenameFiles.ValidateLeaf(leaf);
                if(!string.Equals(Path.GetExtension(leaf),extension,StringComparison.Ordinal))throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_569F4A814F09")+leaf);
                items.Add(new(source,Path.Combine(Path.GetDirectoryName(source)!,leaf),stamp.Length,stamp.LastWriteTimeUtc));stamps.Add(stamp);
            }
            RenameFiles.CheckTargets(items.Select(item=>(item.OriginalPath,item.TargetPath)).ToArray(),ct);
            return new(items.AsReadOnly(),stamps.AsReadOnly());
        }catch(RenameException){throw;}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException)
        {throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_46D2756ED379")+ex.Message,ex);}
    }
    private async Task<RenameReceipt> ApplyCoreAsync(RenamePlan plan,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!plan.HasChanges)throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_BBB64F6BFCA8"));
        var routes=plan.Items.Select(item=>(item.OriginalPath,item.TargetPath)).ToArray();
        RenameFiles.CheckTargets(routes,ct);
        var changes=new List<Change>();var leases=new List<RenameLease>();
        try {
        for(int i=0;i<plan.Items.Count;i++) {
            ct.ThrowIfCancellationRequested();var item=plan.Items[i];
            if(RenameFiles.ReadStamp(item.OriginalPath)!=plan.Stamps[i])throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_F3DF17B47D6D")+item.OriginalPath);
            if(!item.HasChange)continue;
            var lease=await RenameFiles.LockFingerprintAsync(item.OriginalPath,ct).ConfigureAwait(false);leases.Add(lease);var fingerprint=lease.Fingerprint;
            if(!RenameFiles.Matches(plan.Stamps[i],fingerprint))throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_F3DF17B47D6D")+item.OriginalPath);
            changes.Add(new(item.OriginalPath,item.TargetPath,fingerprint));
        }
        // All hashes and snapshots are checked before any mutation; check occupants again after hashing.
        RenameFiles.CheckTargets(routes,ct);
        foreach(var item in changes)Recheck(item);
        Execute(changes,ct);
        return new RenameReceipt(changes.Select(item=>new RenameReceiptItem(item.Initial,item.Target,item.Fingerprint)).ToList().AsReadOnly());
        }finally{foreach(var lease in leases)lease.Dispose();}
    }

    private void Execute(List<Change> changes,CancellationToken ct)
    {
        try {
            foreach(var item in changes) {
                ct.ThrowIfCancellationRequested();Recheck(item);
                Move("stage",item,item.StagingPath);item.WasMoved=true;
            }
            foreach(var item in changes) {ct.ThrowIfCancellationRequested();Recheck(item);Move("commit",item,item.Target);}
            foreach(var item in changes){ct.ThrowIfCancellationRequested();Recheck(item);}
        }catch(Exception ex) {
            var remaining=Rollback(changes);
            if(remaining.Count>0) {
                string locations=string.Join(Environment.NewLine,remaining.Select(item=>$"{item.OriginalPath} → {item.CurrentPath}"+(item.ExpectedFilePresent?"":global::Orbit.Core.Localization.L.Get("Text_F21F44DF115B"))+(string.IsNullOrEmpty(item.Problem)?"":" · "+item.Problem)));
                throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_A05E2A494D83")+Environment.NewLine+locations,ex,remaining);
            }
            if(ex is OperationCanceledException)throw;
            throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_6AA108A2CD37")+ex.Message,ex);
        }
    }
    private List<RenameLocation> Rollback(List<Change> changes)
    {
        var errors=new Dictionary<Change,string>();
        // Rollback also stages first so swaps and case-only names can return without overwriting.
        foreach(var item in changes.Where(item=>item.WasMoved)) {
            try{VerifyIdentity(item);Move("rollback-stage",item,UniqueStaging(item.Initial));}
            catch(Exception ex){errors[item]=ex.Message;}
        }
        foreach(var item in changes.Where(item=>item.WasMoved)) {
            if(string.Equals(item.Current,item.Initial,StringComparison.Ordinal))continue;
            try{VerifyIdentity(item);Move("rollback-restore",item,item.Initial);errors.Remove(item);}
            catch(Exception ex){errors[item]=ex.Message;}
        }
        var remaining=new List<RenameLocation>();
        foreach(var item in changes.Where(item=>item.WasMoved)) {
            bool verified=true;try{VerifyIdentity(item);}catch(Exception ex){verified=false;errors[item]=ex.Message;}
            if(!string.Equals(item.Current,item.Initial,StringComparison.Ordinal)||!verified)
                remaining.Add(new(item.Initial,item.Current,verified,errors.GetValueOrDefault(item)));
        }
        return remaining;
    }
    private void Move(string phase,Change item,string target)
    {
        BeforeMove?.Invoke(phase,item.Current,target);
        if(phase.StartsWith("rollback-",StringComparison.Ordinal))VerifyIdentity(item);else Recheck(item);
        RenameFiles.SafeAttributes(Path.GetDirectoryName(target)!,false);
        File.Move(item.Current,target,overwrite:false);item.Current=target;
    }
    private static void Recheck(Change item)
    {
        if(!RenameFiles.Matches(RenameFiles.ReadStamp(item.Current),item.Fingerprint))throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_5B9E8AD3B4E7")+item.Current);
    }
    private static void VerifyIdentity(Change item)
    {
        var actual=RenameFiles.ReadStamp(item.Current);
        if(item.Fingerprint.FileIdentity!=null&&actual.FileIdentity!=item.Fingerprint.FileIdentity)
            throw new RenameException(global::Orbit.Core.Localization.L.Get("Text_39B9E5844FF2")+item.Current);
    }
    private static string UniqueStaging(string source)=>Path.Combine(Path.GetDirectoryName(source)!,".orbit-rename-"+Guid.NewGuid().ToString("N")+".tmp");
    private sealed class Change(string initial,string target,RenameFingerprint fingerprint)
    {
        public string Initial{get;}=initial;
        public string Target{get;}=target;
        public RenameFingerprint Fingerprint{get;}=fingerprint;
        public string StagingPath{get;}=UniqueStaging(initial);
        public string Current{get;set;}=initial;
        public bool WasMoved{get;set;}
    }
}
