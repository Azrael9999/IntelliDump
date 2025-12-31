# IntelliDump

IntelliDump is an offline C# utility that inspects IIS/.NET dump files with a local heuristic engine to surface likely root causes for crashes, slowdowns, lock contention, and CPU or memory pressure. It can optionally surface SQL/XML (or any other large strings) captured on thread stacks or the heap for faster RCA—while clamping output to stay resilient on massive dumps.

## Quick start

```bash
# Build and run the analyzer against a dump
dotnet run --project src/IntelliDump.App /path/to/iis-worker.dmp

# Emit SQL/XML strings seen on thread stacks/heap (configurable count/length; capped internally for safety)
 dotnet run --project src/IntelliDump.App /path/to/iis-worker.dmp --strings 10 --heap-strings 10 --max-string-length 64000

# Launch the GUI (Avalonia) for interactive analysis and PDF export
dotnet run --project src/IntelliDump.App

# Export a PDF from the GUI (menu/toolbar) once a dump is loaded
```

The tool never calls external services; all processing happens locally using [Microsoft.Diagnostics.Runtime](https://www.nuget.org/packages/Microsoft.Diagnostics.Runtime) to read the dump and a rules-based reasoner to rank findings.

## What it checks

- Crashes/unhandled exceptions (faulting thread surfaced first).
- Memory pressure signals: total heap size, LOH growth, pinned pressure, Gen2 dominance, GC mode.
- Blocking/locks: sync blocks, waiting threads, deadlock candidates, non-monitor blocking hotspots.
- Thread health: running vs. waiting, GC/finalizer threads, threadpool starvation cues, per-thread CPU time (when available).
- String signals: stack/heap strings with source, duplication frequency, and head+tail preservation on truncation.
- Heap composition: top types with coverage percentage and total heap objects; dominant-type detection.
- Modules: size-based anomalies, truncation coverage, instrumentation/profiler hints, and native footprint cues when modules dwarf managed heap.
- Data quality: categorized warnings for heap availability, thread truncation, stack read partials, string clamps/dedupe, histogram/module truncation.
- Wait classification: HTTP and SQL waits, sync-over-async Task waits, and ThreadPool gate congestion signals; async deadlock/sync-over-async warnings when Task.Wait/Result patterns dominate.
- Native/mixed-mode hints: warns when managed heap is small but native/module footprint is large to prompt native-memory/handle investigations.

## Output

- **Console:** Findings with severity and evidence, thread/GC summaries, top stacks, string summaries (including duplicate hotspots), heap/module truncation transparency, and categorized warnings. Deadlocks print with a critical banner.
- **GUI:** Interactive view with the same signals plus PDF export.
- **PDF:** Hyperlinked findings, per-thread stack fidelity (frames read/requested, CPU time), string metadata and top duplicates, heap/module coverage percentages, and grouped warnings so you know what data was truncated.
