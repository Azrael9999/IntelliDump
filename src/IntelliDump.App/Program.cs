using Avalonia;
using IntelliDump;
using IntelliDump.Diagnostics;
using IntelliDump.Output;
using IntelliDump.Reasoning;

var builtApp = BuildAvaloniaApp();

if (args.Length > 0 && args[0] != "--gui")
{
    RunHeadless(args);
    return;
}

builtApp.StartWithClassicDesktopLifetime(args);

static void RunHeadless(string[] args)
{
    try
    {
        var options = Options.FromArgs(args);
        var loader = new DumpLoader();
        var snapshot = loader.Load(options);
        var issues = new LocalReasoner().Analyze(snapshot);

        var reporter = new ConsoleReporter();
        reporter.Render(snapshot, issues);

        if (!string.IsNullOrWhiteSpace(options.JsonOutputPath))
        {
            var payload = new
            {
                snapshot,
                issues
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(options.JsonOutputPath!, json);
            Console.WriteLine($"JSON report written to {options.JsonOutputPath}");
        }
    }
    catch (ArgumentException ex) when (ex.Message == "help")
    {
        PrintHelp();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("Failed to analyze dump.");
        Console.Error.WriteLine(ex.Message);
        Console.ResetColor();
    }
}

static void PrintHelp()
{
    Console.WriteLine("IntelliDump - offline intelligent IIS dump analyzer (GUI + CLI)");
    Console.WriteLine();
    Console.WriteLine("Usage (GUI):");
    Console.WriteLine("  intellidump             # launches the Avalonia UI");
    Console.WriteLine("Usage (CLI):");
    Console.WriteLine("  intellidump <path-to-dump> [--strings <count>] [--max-string-length <chars>]");
    Console.WriteLine("                          [--heap-strings <count>] [--heap-histogram <types>]");
    Console.WriteLine("                          [--max-stack-frames <frames>] [--top-stack-threads <threads>]");
    Console.WriteLine("                          [--json <path>]");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine("  dotnet run --project src/IntelliDump.App ./iis-crash.dmp --strings 5 --max-string-length 120000 --heap-histogram 15 --json report.json");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --strings (-s)           Number of string values to extract from thread stacks (SQL, XML, etc.). Default: 0 (disabled).");
    Console.WriteLine("  --max-string-length      Maximum characters to emit per string to prevent runaway output. Default: 65536.");
    Console.WriteLine("  --heap-strings           Number of large strings to extract from the managed heap (in addition to stack strings). Default: 0 (disabled).");
    Console.WriteLine("  --heap-histogram         Top N managed types by size to surface from the heap. Default: 0 (disabled).");
    Console.WriteLine("  --max-stack-frames       Maximum frames to capture per thread stack (for top stack threads). Default: 30.");
    Console.WriteLine("  --top-stack-threads      Number of threads to show stack traces for (ordered by lock count then id). Default: 5.");
    Console.WriteLine("  --json                   Write a JSON report to the given path.");
    Console.WriteLine();
    Console.WriteLine("The tool runs entirely offline and uses heuristic reasoning");
    Console.WriteLine("to highlight potential crashes, locks, CPU spikes, memory pressure, deadlocks, hotspots, and captures strings when requested.");
}

static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
