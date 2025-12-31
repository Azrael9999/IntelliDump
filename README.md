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

# Ask a local LLM (defaults to phi3:mini via Ollama) to narrate the findings
dotnet run --project src/IntelliDump.App /path/to/iis-worker.dmp --ai --ai-model phi3:mini
```

The tool never calls external services; all processing happens locally using [Microsoft.Diagnostics.Runtime](https://www.nuget.org/packages/Microsoft.Diagnostics.Runtime) to read the dump and a rules-based reasoner to rank findings. When `--ai` is enabled, IntelliDump posts the bounded context to a **local** text-generation endpoint (Ollama-compatible) so you can run a free model offline—tested with `phi3:mini`.

## Local AI summary (optional)

- Pull a small model locally (e.g., `ollama run phi3:mini`) so it is cached.
- Run the CLI with `--ai` (and optional `--ai-model`, `--ai-endpoint`, `--ai-context-chars`), or tick “Enable AI summary” in the GUI.
- IntelliDump sends a compact prompt containing heuristic findings, GC/thread stats, top strings, modules, and warnings to the local endpoint (default `http://localhost:11434/api/generate`). No cloud traffic is used.
- The AI runs two passes: a quick summary, then a “problem check” loop that refines the findings into concrete suspected problems and fixes—and the GUI surfaces only those problems. You can also ask free-form questions after analysis; the AI answers using the dump evidence.

## Running in Visual Studio

1. Open `IntelliDump.sln` in Visual Studio 2022 or newer.
2. Set the startup project to **IntelliDump.App** (right-click → “Set as Startup Project”).
3. Choose **Debug** → **Start Debugging** to launch the Avalonia GUI, or run `dotnet run` from the Package Manager Console for CLI usage.
4. To enable local AI, ensure an Ollama-compatible endpoint is running (default `http://localhost:11434/api/generate`) and tick “Enable AI analysis” in the GUI, or pass `--ai` in the CLI arguments.

## Where to put the AI model

- IntelliDump talks to an Ollama-compatible endpoint. With Ollama, models are stored automatically under your user profile (e.g., `%USERPROFILE%/.ollama/models` on Windows or `~/.ollama/models` on Linux/macOS).
- To install a model locally, run `ollama pull phi3:mini` (or your chosen model) on the same machine that will run IntelliDump. No extra configuration in IntelliDump is needed as long as the endpoint can serve that model.
- If you host a compatible server elsewhere, update the endpoint (`--ai-endpoint` in CLI or the GUI field) to point to that host; just ensure it can access the model on its own filesystem.

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
