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
        var threads = BuildThreadSnapshots(runtime).ToList();
        var gc = BuildGcSnapshot(runtime);
        var blocking = BuildBlockingSummary(runtime);
        var strings = ExtractStrings(runtime, options.MaxStringsToCapture, options.MaxStringLength);

        return new DumpSnapshot(
            options.DumpPath,
            $"{clrInfo.Flavor} - {clrInfo.Version}",
            threads,
            gc,
            blocking,
            strings);
    }

    private static IEnumerable<ThreadSnapshot> BuildThreadSnapshots(ClrRuntime runtime)
    {
        foreach (var thread in runtime.Threads.Where(t => t.IsAlive))
        {
            var state = thread.State.ToString();
            var exceptionDescription = thread.CurrentException is null
                ? null
                : $"{thread.CurrentException.Type?.Name}: {thread.CurrentException.Message}";

            yield return new ThreadSnapshot(
                thread.ManagedThreadId,
                state,
                (int)thread.LockCount,
                exceptionDescription,
                thread.IsFinalizer,
                thread.IsGc);
        }
    }

    private static GcSnapshot BuildGcSnapshot(ClrRuntime runtime)
    {
        var heap = runtime.Heap;
        ulong totalHeapBytes = 0;
        ulong lohBytes = 0;
        int segments = 0;

        if (heap is not null)
        {
            foreach (var segment in heap.Segments)
            {
                segments++;
                totalHeapBytes += segment.Length;
                if (segment.Kind == GCSegmentKind.Large)
                {
                    lohBytes += segment.Length;
                }
            }
        }

        return new GcSnapshot(totalHeapBytes, lohBytes, segments, heap?.IsServer ?? false);
    }

    private static BlockingSummary BuildBlockingSummary(ClrRuntime runtime)
    {
        var syncBlocks = runtime.Heap.EnumerateSyncBlocks().ToList();
        var waiting = syncBlocks.Sum(sb => sb.WaitingThreadCount);
        return new BlockingSummary(syncBlocks.Count, waiting);
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
}
