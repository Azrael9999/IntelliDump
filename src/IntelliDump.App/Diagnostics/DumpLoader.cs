using Microsoft.Diagnostics.Runtime;

namespace IntelliDump.Diagnostics;

public sealed class DumpLoader
{
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
        var threads = BuildThreadSnapshots(runtime, options.MaxStackFrames, options.TopStackThreads).ToList();
        var gc = BuildGcSnapshot(runtime);
        var blocking = BuildBlockingSummary(runtime);
        var strings = ExtractStrings(runtime, options.MaxStringsToCapture, options.MaxStringLength);
        var deadlocks = FindDeadlocks(runtime).ToList();
        var histogram = BuildHeapHistogram(runtime, options.HeapHistogramCount);
        var heapStrings = ExtractHeapStrings(runtime, options.HeapStringLimit, options.MaxStringLength);
        strings = strings.Concat(heapStrings).ToList();
        var modules = ReadModules(runtime).ToList();
        var warnings = BuildWarnings(runtime).ToList();

        return new DumpSnapshot(
            options.DumpPath,
            $"{clrInfo.Flavor} - {clrInfo.Version}",
            threads,
            gc,
            blocking,
            strings,
            deadlocks,
            histogram,
            modules,
            warnings);
    }

    private static IEnumerable<ThreadSnapshot> BuildThreadSnapshots(ClrRuntime runtime, int maxStackFrames, int topStackThreads)
    {
        var sorted = runtime.Threads
            .Where(t => t.IsAlive)
            .OrderByDescending(t => t.LockCount)
            .ThenByDescending(t => t.ManagedThreadId)
            .Take(Math.Max(topStackThreads, 10)); // keep reasonable cap to avoid flooding output

        foreach (var thread in sorted)
        {
            var state = thread.State.ToString();
            var exceptionDescription = thread.CurrentException is null
                ? null
                : $"{thread.CurrentException.Type?.Name}: {thread.CurrentException.Message}";

            var stack = new List<string>();
            try
            {
                foreach (var frame in thread.EnumerateStackTrace().Take(maxStackFrames))
                {
                    stack.Add(frame.ToString() ?? string.Empty);
                }
            }
            catch
            {
                // ignore stack read issues
            }

            yield return new ThreadSnapshot(
                thread.ManagedThreadId,
                state,
                (int)thread.LockCount,
                exceptionDescription,
                thread.IsFinalizer,
                thread.IsGc,
                stack);
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

    private static IReadOnlyList<NotableString> ExtractStrings(ClrRuntime runtime, int maxStringsToCapture, int maxStringLength)
    {
        if (maxStringsToCapture <= 0)
        {
            return Array.Empty<NotableString>();
        }

        var results = new List<NotableString>(Math.Min(maxStringsToCapture, 100));
        foreach (var thread in runtime.Threads.Where(t => t.IsAlive))
        {
            foreach (var root in thread.EnumerateStackRoots())
            {
                if (results.Count >= maxStringsToCapture)
                {
                    return results;
                }

                var obj = runtime.Heap.GetObject(root.Object);
                if (!obj.IsValid || obj.Type?.IsString != true)
                {
                    continue;
                }

                string? value = null;
                try
                {
                    value = obj.AsString(maxStringLength + 1);
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
                if (originalLength > maxStringLength)
                {
                    value = value[..maxStringLength];
                    truncated = true;
                }

                results.Add(new NotableString(thread.ManagedThreadId, value, originalLength, truncated));
            }
        }

        return results;
    }

    private static IReadOnlyList<NotableString> ExtractHeapStrings(ClrRuntime runtime, int limit, int maxStringLength)
    {
        if (limit <= 0 || !runtime.Heap.CanWalkHeap)
        {
            return Array.Empty<NotableString>();
        }

        var heapStrings = new List<NotableString>(limit);
        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            if (heapStrings.Count >= limit)
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
                value = obj.AsString(maxStringLength + 1);
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
            if (original > maxStringLength)
            {
                value = value[..maxStringLength];
                truncated = true;
            }

            heapStrings.Add(new NotableString(0, value, original, truncated));
        }

        return heapStrings;
    }

    private static IReadOnlyList<HeapTypeStat> BuildHeapHistogram(ClrRuntime runtime, int maxTypes)
    {
        if (maxTypes <= 0 || !runtime.Heap.CanWalkHeap)
        {
            return Array.Empty<HeapTypeStat>();
        }

        var totals = new Dictionary<string, (ulong size, int count)>(StringComparer.Ordinal);
        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
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

        return totals
            .Select(kvp => new HeapTypeStat(kvp.Key, kvp.Value.size, kvp.Value.count))
            .OrderByDescending(h => h.TotalSize)
            .Take(maxTypes)
            .ToList();
    }

    private static IEnumerable<ModuleInfo> ReadModules(ClrRuntime runtime)
    {
        foreach (var module in runtime.EnumerateModules())
        {
            yield return new ModuleInfo(module.Name ?? "unknown", (ulong)module.Size);
        }
    }

    private static IEnumerable<string> BuildWarnings(ClrRuntime runtime)
    {
        if (!runtime.Heap.CanWalkHeap)
        {
            yield return "Heap is not fully available in this dump; memory-related signals may be incomplete.";
        }
    }
}
