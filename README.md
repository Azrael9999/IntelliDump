# IntelliDump

IntelliDump is an offline C# utility that inspects IIS/.NET dump files with a local heuristic engine to surface likely root causes for crashes, slowdowns, lock contention, and CPU or memory pressure. It can optionally surface SQL/XML (or any other large strings) captured on thread stacks for faster RCA.

## Quick start

```bash
# Build and run the analyzer against a dump
dotnet run --project src/IntelliDump.App /path/to/iis-worker.dmp

# Emit SQL/XML strings seen on thread stacks (up to 5 strings capped at 120000 chars each)
dotnet run --project src/IntelliDump.App /path/to/iis-worker.dmp --strings 5 --max-string-length 120000
```

The tool never calls external services; all processing happens locally using [Microsoft.Diagnostics.Runtime](https://www.nuget.org/packages/Microsoft.Diagnostics.Runtime) to read the dump and a rules-based reasoner to rank findings.

## What it checks

- Unhandled exceptions that likely triggered a crash.
- Managed heap size and Large Object Heap growth to flag memory pressure.
- Blocking/lock interactions that may signal deadlocks or thread starvation.
- Thread pool/GC signals and high numbers of running threads that hint at CPU spikes or sync-over-async patterns.
- Optional capture of large strings (XML/SQL/payloads) from thread stacks with configurable limits to avoid runaway output.

## Output

The console output highlights:

- A summary of the dump/runtime.
- Findings with severity, evidence, and remediation suggestions.
- Thread and GC overviews to guide deeper stack inspection with your preferred debugger.
