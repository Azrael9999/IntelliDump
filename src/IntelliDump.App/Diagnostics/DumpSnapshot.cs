using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace IntelliDump.Diagnostics;

public sealed record ThreadSnapshot(
    int ManagedId,
    string State,
    int LockCount,
    string? CurrentException,
    bool IsFinalizer,
    bool IsGcThread,
    IReadOnlyList<string> Stack,
    int CapturedStackFrames,
    int RequestedStackFrames,
    double? CpuTimeMs);

public enum StringSource
{
    Stack,
    Heap,
    StackAndHeap
}

public sealed record NotableString(
    IReadOnlyList<int> ThreadIds,
    string Value,
    int TotalLength,
    bool WasTruncated,
    StringSource Source,
    int Occurrences);

public enum WarningCategory
{
    HeapUnavailable,
    ThreadSelection,
    ThreadTruncation,
    StackReadPartial,
    StringClamp,
    StringDedupe,
    HeapStringClamp,
    ModuleClamp,
    HeapHistogramClamp,
    Other
}

public sealed record DataWarning(WarningCategory Category, string Message);

public sealed record GcSnapshot(
    ulong TotalHeapBytes,
    ulong LargeObjectHeapBytes,
    int SegmentCount,
    bool IsServerGc,
    ulong Gen0Bytes,
    ulong Gen1Bytes,
    ulong Gen2Bytes,
    ulong PinnedBytes);

public sealed record BlockingSummary(int SyncBlockCount, int WaitingThreadCount);

public sealed record DeadlockCandidate(
    int? OwnerThreadId,
    int WaitingThreads,
    ulong ObjectAddress);

public sealed record HeapTypeStat(string TypeName, ulong TotalSize, int Count);

public sealed record ModuleInfo(string Name, ulong Size);

public sealed record DumpSnapshot(
    string DumpPath,
    string? RuntimeDescription,
    int TotalThreadCount,
    IReadOnlyList<ThreadSnapshot> Threads,
    GcSnapshot Gc,
    BlockingSummary Blocking,
    IReadOnlyList<NotableString> NotableStrings,
    IReadOnlyList<DeadlockCandidate> Deadlocks,
    IReadOnlyList<HeapTypeStat> HeapHistogram,
    IReadOnlyList<ModuleInfo> Modules,
    int TotalHeapTypeCount,
    int TotalModuleCount,
    long TotalModuleBytes,
    double ModuleCoverageShown,
    int UniqueStringCount,
    int TotalStringOccurrences,
    int StackStringOccurrences,
    int HeapStringOccurrences,
    int TotalHeapObjectCount,
    double HeapHistogramCoverage,
    IReadOnlyList<DataWarning> Warnings)
{
    public ThreadSnapshot? FaultingThread =>
        Threads.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CurrentException));

    public IReadOnlyList<ThreadSnapshot> FinalizerThreads =>
        new ReadOnlyCollection<ThreadSnapshot>(Threads.Where(t => t.IsFinalizer).ToList());

    public IReadOnlyList<NotableString> Strings =>
        new ReadOnlyCollection<NotableString>(NotableStrings.ToList());

    public IReadOnlyList<DeadlockCandidate> DeadlockCandidates =>
        new ReadOnlyCollection<DeadlockCandidate>(Deadlocks.ToList());

    public IReadOnlyList<HeapTypeStat> HeapTypes =>
        new ReadOnlyCollection<HeapTypeStat>(HeapHistogram.ToList());

    public IReadOnlyList<ModuleInfo> LoadedModules =>
        new ReadOnlyCollection<ModuleInfo>(Modules.ToList());
}
