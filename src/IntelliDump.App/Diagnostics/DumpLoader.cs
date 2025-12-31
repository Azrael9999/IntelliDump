using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Diagnostics.Runtime;

namespace IntelliDump.Diagnostics;

public sealed class DumpLoader
{
    private const int StringCaptureHardCap = 2000;
    private const int StringLengthHardCap = 32_768;

    public DumpSnapshot Load(Options options)
    {
        if (string.IsNullOrWhiteSpace(options.DumpPath))
        {
            throw new ArgumentException("A dump path must be provided.", nameof(options.DumpPath));
        }

        if (!File.Exists(options.DumpPath))
        {
            throw new FileNotFoundException("The specified dump file does not exist.", options.DumpPath);
        }

        using var target = DataTarget.LoadDump(options.DumpPath);
        var clrInfo = target.ClrVersions.FirstOrDefault()
                      ?? throw new InvalidOperationException("No CLR runtime could be located in the dump.");

        using var runtime = clrInfo.CreateRuntime();
        var warnings = BuildWarnings(runtime).ToList();
        var threads = BuildThreadSnapshots(runtime, options.MaxStackFrames, options.TopStackThreads, warnings).ToList();
        var gc = BuildGcSnapshot(runtime);
        var blocking = BuildBlockingSummary(runtime);
        var (strings, aggregates, stackOwners) = ExtractStrings(runtime, options.MaxStringsToCapture, options.MaxStringLength, warnings);
        var deadlocks = FindDeadlocks(runtime).ToList();
        var (histogram, totalHeapTypeCount, totalHeapObjectCount, topTypeBytes) = BuildHeapHistogram(runtime, options.HeapHistogramCount);
        strings = ExtractHeapStrings(runtime, options.HeapStringLimit, options.MaxStringLength, warnings, aggregates, stackOwners);
        var modules = ReadModules(runtime).ToList();
        var totalStringOccurrences = aggregates.Values.Sum(a => a.Occurrences);
        var stackStringOccurrences = aggregates.Values.Where(a => a.Source is StringSource.Stack or StringSource.StackAndHeap).Sum(a => a.Occurrences);
        var heapStringOccurrences = aggregates.Values.Where(a => a.Source is StringSource.Heap or StringSource.StackAndHeap).Sum(a => a.Occurrences);
        var topTypeCoverage = ComputeHistogramCoverage(topTypeBytes, gc.TotalHeapBytes);
        const int moduleDisplay = 20;
        var totalModuleBytes = modules.Aggregate<ModuleInfo, long>(0, (sum, m) => sum + (long)m.Size);
        var topModuleBytes = modules.OrderByDescending(m => m.Size).Take(moduleDisplay).Aggregate<ModuleInfo, long>(0, (sum, m) => sum + (long)m.Size);
        var moduleCoverageShown = totalModuleBytes == 0 ? 0 : Math.Min(1.0, (double)topModuleBytes / totalModuleBytes);
        if (modules.Count > moduleDisplay)
        {
            warnings.Add(new DataWarning(WarningCategory.ModuleClamp, $"Modules truncated in reports to top {moduleDisplay} of {modules.Count}."));
        }

        const int heapDisplay = 10;
        if (totalHeapTypeCount > heapDisplay)
        {
            warnings.Add(new DataWarning(WarningCategory.HeapHistogramClamp, $"Heap histogram truncated to top {heapDisplay} of {totalHeapTypeCount} types (coverage {(topTypeCoverage * 100):N1}%)."));
        }

        return new DumpSnapshot(
            options.DumpPath,
            $"{clrInfo.Flavor} - {clrInfo.Version}",
            runtime.Threads.Count(t => t.IsAlive),
            threads,
            gc,
            blocking,
            strings,
            deadlocks,
            histogram,
            modules,
            totalHeapTypeCount,
            modules.Count,
            totalModuleBytes,
            moduleCoverageShown,
            strings.Count,
            totalStringOccurrences,
            stackStringOccurrences,
            heapStringOccurrences,
            totalHeapObjectCount,
            topTypeCoverage,
            SortWarnings(warnings));
    }

    private static readonly Lazy<PropertyInfo?> CpuTimeProperty = new(() =>
    {
        var names = new[] { "TotalProcessorTime", "CpuTime", "TotalProcessorTimeMs" };
        foreach (var name in names)
        {
            var prop = typeof(ClrThread).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is not null)
            {
                return prop;
            }
        }

        return null;
    });

    private static IEnumerable<ThreadSnapshot> BuildThreadSnapshots(ClrRuntime runtime, int maxStackFrames, int topStackThreads, ICollection<DataWarning> warnings)
    {
        var aliveThreads = runtime.Threads.Where(t => t.IsAlive).ToList();
        var maxThreads = Math.Max(topStackThreads, 10);

        static int ScoreThread(ClrThread thread)
        {
            var score = 0;
            if (thread.CurrentException is not null)
            {
                score += 1000;
            }

            var state = thread.State.ToString();
            if (state.Contains("Running", StringComparison.OrdinalIgnoreCase))
            {
                score += 200;
            }
            else if (state.Contains("Wait", StringComparison.OrdinalIgnoreCase) || state.Contains("Sleep", StringComparison.OrdinalIgnoreCase))
            {
                score += 120;
            }

            if (thread.IsFinalizer)
            {
                score += 80;
            }

            if (thread.IsGc)
            {
                score += 40;
            }

            score += (int)Math.Min(thread.LockCount * 5, 200);

            return score;
        }

        var prioritized = aliveThreads
            .Select(t => new { Thread = t, Score = ScoreThread(t) })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Thread.LockCount)
            .ThenByDescending(x => x.Thread.ManagedThreadId)
            .ToList();

        // ensure inclusion of faulting thread and a slice of running/waiting pools
        var faulting = prioritized.FirstOrDefault(x => x.Thread.CurrentException is not null);
        var runningThreads = prioritized.Where(x => x.Thread.State.ToString().Contains("Running", StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
        var waitingThreads = prioritized.Where(x => x.Thread.State.ToString().Contains("Wait", StringComparison.OrdinalIgnoreCase) || x.Thread.State.ToString().Contains("Sleep", StringComparison.OrdinalIgnoreCase)).Take(5).ToList();

        var forced = new HashSet<int>();
        void AddForced(IEnumerable<dynamic> list)
        {
            foreach (var item in list)
            {
                forced.Add(item.Thread.ManagedThreadId);
            }
        }

        if (faulting is not null)
        {
            forced.Add(faulting.Thread.ManagedThreadId);
        }
        AddForced(runningThreads);
        AddForced(waitingThreads);

        prioritized = prioritized
            .OrderByDescending(x => forced.Contains(x.Thread.ManagedThreadId))
            .ThenByDescending(x => x.Score)
            .ThenByDescending(x => x.Thread.LockCount)
            .ThenByDescending(x => x.Thread.ManagedThreadId)
            .ToList();

        if (prioritized.Count > maxThreads)
        {
            var dropped = prioritized.Skip(maxThreads).Select(x => x.Thread.ManagedThreadId).Take(20).ToList();
            var stateCounts = prioritized.Skip(maxThreads)
                .GroupBy(x => x.Thread.State.ToString())
                .Select(g => $"{g.Key}:{g.Count()}")
                .ToList();

            warnings.Add(new DataWarning(
                WarningCategory.ThreadTruncation,
                $"Threads truncated to {maxThreads} of {prioritized.Count} (priority exceptions/running/waiting/locks). Dropped IDs: {string.Join(", ", dropped)}{(prioritized.Count - maxThreads > dropped.Count ? " ..." : "")}; states: {string.Join(", ", stateCounts)}"));
        }

        var stackReadIssues = new List<int>();
        foreach (var item in prioritized.Take(maxThreads))
        {
            var thread = item.Thread;
            var state = thread.State.ToString();
            var exceptionDescription = thread.CurrentException is null
                ? null
                : $"{thread.CurrentException.Type?.Name}: {thread.CurrentException.Message}";

            var stack = new List<string>();
            var framesRead = 0;
            try
            {
                foreach (var frame in thread.EnumerateStackTrace().Take(maxStackFrames))
                {
                    stack.Add(frame.ToString() ?? string.Empty);
                    framesRead++;
                }
            }
            catch
            {
                stackReadIssues.Add(thread.ManagedThreadId);
            }

            yield return new ThreadSnapshot(
                thread.ManagedThreadId,
                state,
                (int)thread.LockCount,
                exceptionDescription,
                thread.IsFinalizer,
                thread.IsGc,
                stack,
                framesRead,
                maxStackFrames,
                TryGetCpuTimeMs(thread));
        }

        if (stackReadIssues.Count > 0)
        {
            warnings.Add(new DataWarning(
                WarningCategory.StackReadPartial,
                $"Stack frames were partially read for threads: {string.Join(", ", stackReadIssues.Take(10))}{(stackReadIssues.Count > 10 ? " ..." : "")}."));
        }
    }

    private static double? TryGetCpuTimeMs(ClrThread thread)
    {
        var prop = CpuTimeProperty.Value;
        if (prop is null)
        {
            return null;
        }

        try
        {
            var value = prop.GetValue(thread);
            return value switch
            {
                TimeSpan ts => ts.TotalMilliseconds,
                double d => d,
                float f => f,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static GcSnapshot BuildGcSnapshot(ClrRuntime runtime)
    {
        var heap = runtime.Heap;
        ulong totalHeapBytes = 0;
        ulong lohBytes = 0;
        int segments = 0;
        ulong gen0 = 0;
        ulong gen1 = 0;
        ulong gen2 = 0;
        ulong pinned = 0;

        if (heap is not null && heap.CanWalkHeap)
        {
            foreach (var segment in heap.Segments)
            {
                segments++;
                totalHeapBytes += segment.Length;
                switch (segment.Kind)
                {
                    case GCSegmentKind.Generation0:
                        gen0 += segment.Length;
                        break;
                    case GCSegmentKind.Generation1:
                        gen1 += segment.Length;
                        break;
                    case GCSegmentKind.Generation2:
                        gen2 += segment.Length;
                        break;
                    case GCSegmentKind.Large:
                        lohBytes += segment.Length;
                        break;
                    case GCSegmentKind.Pinned:
                        pinned += segment.Length;
                        break;
                }
            }
        }

        return new GcSnapshot(totalHeapBytes, lohBytes, segments, heap?.IsServer ?? false, gen0, gen1, gen2, pinned);
    }

    private static BlockingSummary BuildBlockingSummary(ClrRuntime runtime)
    {
        var syncBlocks = runtime.Heap.EnumerateSyncBlocks().ToList();
        var waiting = syncBlocks.Sum(sb => sb.WaitingThreadCount);
        return new BlockingSummary(syncBlocks.Count, waiting);
    }

    private static IEnumerable<DeadlockCandidate> FindDeadlocks(ClrRuntime runtime)
    {
        var syncBlocks = runtime.Heap.EnumerateSyncBlocks().ToList();
        var threadsByAddress = runtime.Threads.ToDictionary(t => t.Address, t => t.ManagedThreadId);

        foreach (var block in syncBlocks.Where(b => b.WaitingThreadCount > 0 || b.IsMonitorHeld))
        {
            threadsByAddress.TryGetValue(block.HoldingThreadAddress, out var owner);
            yield return new DeadlockCandidate(
                owner == 0 ? null : owner,
                block.WaitingThreadCount,
                block.Object);
        }
    }

    private sealed class StringAggregate
    {
        public StringAggregate(string value, int totalLength, bool truncated, StringSource source)
        {
            Value = value;
            TotalLength = totalLength;
            WasTruncated = truncated;
            Source = source;
        }

        public string Value { get; }
        public int TotalLength { get; set; }
        public bool WasTruncated { get; set; }
        public StringSource Source { get; set; }
        public int Occurrences { get; set; } = 1;
        public HashSet<int> ThreadIds { get; } = new();
    }

    private static (IReadOnlyList<NotableString> strings, Dictionary<string, StringAggregate> aggregates, Dictionary<ulong, HashSet<int>> stackOwners) ExtractStrings(
        ClrRuntime runtime,
        int maxStringsToCapture,
        int maxStringLength,
        ICollection<DataWarning> warnings)
    {
        if (maxStringsToCapture <= 0)
        {
            return (Array.Empty<NotableString>(), new Dictionary<string, StringAggregate>(0, StringComparer.Ordinal), new Dictionary<ulong, HashSet<int>>());
        }

        var captureLimit = Math.Min(maxStringsToCapture, StringCaptureHardCap);
        if (maxStringsToCapture > StringCaptureHardCap)
        {
            warnings.Add(new DataWarning(WarningCategory.StringClamp, $"String capture limit clamped to {StringCaptureHardCap:N0} to avoid excessive memory usage."));
        }

        var effectiveMaxLength = Math.Min(maxStringLength, StringLengthHardCap);
        if (maxStringLength > StringLengthHardCap)
        {
            warnings.Add(new DataWarning(WarningCategory.StringClamp, $"String length limit clamped to {StringLengthHardCap:N0} characters; longer strings will be truncated."));
        }

        var aggregates = new Dictionary<string, StringAggregate>(Math.Min(captureLimit, 1024), StringComparer.Ordinal);
        var stackOwners = new Dictionary<ulong, HashSet<int>>();
        var deduped = 0;

        CollectStringsFromStacks(runtime, captureLimit, effectiveMaxLength, aggregates, ref deduped, stackOwners);

        if (deduped > 0)
        {
            warnings.Add(new DataWarning(WarningCategory.StringDedupe, $"Suppressed {deduped:N0} duplicate stack strings to keep output manageable."));
        }

        var results = aggregates.Values
            .Select(a => new NotableString(
                a.ThreadIds.ToList(),
                a.Value,
                a.TotalLength,
                a.WasTruncated,
                a.Source,
                a.Occurrences))
            .ToList();

        return (results, aggregates, stackOwners);
    }

    private static void CollectStringsFromStacks(
        ClrRuntime runtime,
        int captureLimit,
        int effectiveMaxLength,
        IDictionary<string, StringAggregate> aggregates,
        ref int deduped,
        IDictionary<ulong, HashSet<int>> stackOwners)
    {
        foreach (var thread in runtime.Threads.Where(t => t.IsAlive))
        {
            foreach (var root in thread.EnumerateStackRoots())
            {
                if (aggregates.Count >= captureLimit)
                {
                    return;
                }

                if (!stackOwners.TryGetValue(root.Object, out var owners))
                {
                    owners = new HashSet<int>();
                    stackOwners[root.Object] = owners;
                }
                owners.Add(thread.ManagedThreadId);

                var obj = runtime.Heap.GetObject(root.Object);
                if (!obj.IsValid || obj.Type?.IsString != true)
                {
                    continue;
                }

                string? value = null;
                try
                {
                    value = obj.AsString(effectiveMaxLength + 1);
                }
                catch
                {
                    // ignore faulty reads to keep analysis resilient
                }

                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                var originalLength = value.Length;
                var truncated = false;
                if (originalLength > effectiveMaxLength)
                {
                    value = PreserveEnds(value, effectiveMaxLength);
                    truncated = true;
                }

                if (aggregates.TryGetValue(value, out var agg))
                {
                    agg.Occurrences++;
                    agg.ThreadIds.Add(thread.ManagedThreadId);
                    if (agg.Source == StringSource.Heap)
                    {
                        agg.Source = StringSource.StackAndHeap;
                    }
                    deduped++;
                    continue;
                }

                var aggregate = new StringAggregate(value, originalLength, truncated, StringSource.Stack);
                aggregate.ThreadIds.Add(thread.ManagedThreadId);
                aggregates[value] = aggregate;
            }
        }
    }

    private static IReadOnlyList<NotableString> ExtractHeapStrings(
        ClrRuntime runtime,
        int limit,
        int maxStringLength,
        ICollection<DataWarning> warnings,
        Dictionary<string, StringAggregate> aggregates,
        IDictionary<ulong, HashSet<int>> stackOwners)
    {
        if (limit <= 0 || !runtime.Heap.CanWalkHeap)
        {
            return aggregates.Values
                .Select(a => new NotableString(
                    a.ThreadIds.ToList(),
                    a.Value,
                    a.TotalLength,
                    a.WasTruncated,
                    a.Source,
                    a.Occurrences))
                .ToList();
        }

        var startingCount = aggregates.Count;
        var available = Math.Max(0, StringCaptureHardCap - startingCount);
        var captureLimit = Math.Min(limit, available);
        if (limit > captureLimit)
        {
            warnings.Add(new DataWarning(WarningCategory.HeapStringClamp, $"Heap string capture limited to {captureLimit:N0} to keep total strings under {StringCaptureHardCap:N0}."));
        }

        var effectiveMaxLength = Math.Min(maxStringLength, StringLengthHardCap);
        var deduped = 0;
        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            if (aggregates.Count >= startingCount + captureLimit)
            {
                break;
            }

            if (obj.Type?.IsString != true)
            {
                continue;
            }

            string? value = null;
            try
            {
                value = obj.AsString(effectiveMaxLength + 1);
            }
            catch
            {
                // ignore
            }

            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var original = value.Length;
            var truncated = false;
            if (original > effectiveMaxLength)
            {
                value = PreserveEnds(value, effectiveMaxLength);
                truncated = true;
            }

            if (aggregates.TryGetValue(value, out var agg))
            {
                agg.Occurrences++;
                if (agg.Source == StringSource.Stack)
                {
                    agg.Source = StringSource.StackAndHeap;
                }
                if (stackOwners.TryGetValue(obj.Address, out var owners))
                {
                    foreach (var tid in owners)
                    {
                        agg.ThreadIds.Add(tid);
                    }
                }
                deduped++;
                continue;
            }

            var aggregate = new StringAggregate(value, original, truncated, StringSource.Heap);
            if (stackOwners.TryGetValue(obj.Address, out var heapOwners))
            {
                foreach (var tid in heapOwners)
                {
                    aggregate.ThreadIds.Add(tid);
                }
            }
            aggregates[value] = aggregate;
        }

        if (deduped > 0)
        {
            warnings.Add(new DataWarning(WarningCategory.StringDedupe, $"Heap string deduplication suppressed {deduped:N0} duplicates."));
        }

        return aggregates.Values
            .Select(a => new NotableString(
                a.ThreadIds.ToList(),
                a.Value,
                a.TotalLength,
                a.WasTruncated,
                a.Source,
                a.Occurrences))
            .ToList();
    }

    private static (IReadOnlyList<HeapTypeStat> histogram, int totalTypes, int totalObjects, ulong topTypeBytes) BuildHeapHistogram(ClrRuntime runtime, int maxTypes)
    {
        if (maxTypes <= 0 || !runtime.Heap.CanWalkHeap)
        {
            return (Array.Empty<HeapTypeStat>(), 0, 0, 0);
        }

        var totals = new Dictionary<string, (ulong size, int count)>(StringComparer.Ordinal);
        var totalObjects = 0;
        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            totalObjects++;
            var typeName = obj.Type?.Name;
            if (string.IsNullOrEmpty(typeName))
            {
                continue;
            }

            if (!totals.TryGetValue(typeName, out var current))
            {
                current = (0, 0);
            }

            totals[typeName] = (current.size + obj.Size, current.count + 1);
        }

        var histogram = totals
            .Select(kvp => new HeapTypeStat(kvp.Key, kvp.Value.size, kvp.Value.count))
            .OrderByDescending(h => h.TotalSize)
            .Take(maxTypes)
            .ToList();

        var topBytes = histogram.Aggregate<HeapTypeStat, ulong>(0, (sum, h) => sum + h.TotalSize);

        return (histogram, totals.Count, totalObjects, topBytes);
    }

    private static IEnumerable<ModuleInfo> ReadModules(ClrRuntime runtime)
    {
        foreach (var module in runtime.EnumerateModules())
        {
            yield return new ModuleInfo(module.Name ?? "unknown", (ulong)module.Size);
        }
    }

    private static double ComputeHistogramCoverage(ulong topBytes, ulong totalHeapBytes)
    {
        if (totalHeapBytes == 0)
        {
            return 0;
        }

        return Math.Min(1.0, (double)topBytes / totalHeapBytes);
    }

    private static IEnumerable<DataWarning> BuildWarnings(ClrRuntime runtime)
    {
        if (!runtime.Heap.CanWalkHeap)
        {
            yield return new DataWarning(WarningCategory.HeapUnavailable, "Heap is not fully available in this dump; memory-related signals may be incomplete.");
        }
    }

    private static IReadOnlyList<DataWarning> SortWarnings(IReadOnlyCollection<DataWarning> warnings)
    {
        var priority = new Dictionary<WarningCategory, int>
        {
            { WarningCategory.HeapUnavailable, 0 },
            { WarningCategory.ThreadTruncation, 1 },
            { WarningCategory.StackReadPartial, 2 },
            { WarningCategory.ThreadSelection, 3 },
            { WarningCategory.StringClamp, 4 },
            { WarningCategory.HeapStringClamp, 5 },
            { WarningCategory.StringDedupe, 6 },
            { WarningCategory.HeapHistogramClamp, 7 },
            { WarningCategory.ModuleClamp, 8 },
            { WarningCategory.Other, 9 }
        };

        return warnings
            .OrderBy(w => priority.TryGetValue(w.Category, out var p) ? p : 9)
            .ThenBy(w => w.Message, StringComparer.Ordinal)
            .ToList();
    }

    private static string PreserveEnds(string value, int limit)
    {
        if (limit <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= limit)
        {
            return value;
        }

        if (limit <= 12)
        {
            return value[..limit];
        }

        const string ellipsis = " ... ";
        var head = Math.Max(8, limit / 2);
        head = Math.Min(head, value.Length - 1);
        var tail = Math.Max(4, limit - head - ellipsis.Length);
        tail = Math.Min(tail, Math.Max(0, value.Length - head));

        var overshoot = head + tail + ellipsis.Length - limit;
        if (overshoot > 0)
        {
            if (tail > overshoot)
            {
                tail -= overshoot;
            }
            else
            {
                head = Math.Max(1, head - (overshoot - tail));
                tail = 0;
            }
        }

        if (head + tail >= value.Length)
        {
            return value[..limit];
        }

        return $"{value[..head]}{ellipsis}{value[^tail..]}";
    }
}
