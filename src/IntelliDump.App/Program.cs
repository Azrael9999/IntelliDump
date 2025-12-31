using Avalonia;
using IntelliDump;
using IntelliDump.Diagnostics;
using IntelliDump.Output;
using IntelliDump.Reasoning;
using QuestPDF.Infrastructure;

var builtApp = BuildAvaloniaApp();
QuestPDF.Settings.License = LicenseType.Community;

if (args.Length > 0 && args[0] != "--gui")
{
    await RunHeadlessAsync(args);
    return;
}

builtApp.StartWithClassicDesktopLifetime(args);

static async Task RunHeadlessAsync(string[] args)
{
    try
    {
        var options = Options.FromArgs(args);
        var loader = new DumpLoader();
        var snapshot = loader.Load(options);
        var issues = new LocalReasoner().Analyze(snapshot);
        string? aiSummary = null;
        string? aiProblems = null;
        string? aiError = null;

        if (options.EnableAi)
        {
            try
            {
                var aiResult = await new AiReasoner().AnalyzeAsync(
                    snapshot,
                    issues,
                    new AiSettings(options.AiModel, options.AiEndpoint, options.AiContextChars),
                    CancellationToken.None);
                aiSummary = aiResult.Summary;
                aiProblems = aiResult.Problems;
                aiError = aiResult.Error;
            }
            catch (Exception ex)
            {
                aiError = $"AI request failed: {ex.Message}";
            }
        }

        var reporter = new ConsoleReporter();
        reporter.Render(snapshot, issues, aiSummary, aiProblems, aiError);

        if (!string.IsNullOrWhiteSpace(options.JsonOutputPath))
        {
            var payload = new
            {
                snapshot,
                issues,
                aiSummary,
                aiProblems,
                aiError
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
    Console.WriteLine("                          [--json <path>] [--ai] [--ai-model <name>] [--ai-endpoint <url>] [--ai-context-chars <chars>]");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine("  dotnet run --project src/IntelliDump.App ./iis-crash.dmp --strings 5 --max-string-length 120000 --heap-histogram 15 --json report.json --ai --ai-model phi3:mini");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --strings (-s)           Number of string values to extract from thread stacks (SQL, XML, etc.). Default: 0 (disabled).");
    Console.WriteLine("  --max-string-length      Maximum characters to emit per string to prevent runaway output. Default: 65536.");
    Console.WriteLine("  --heap-strings           Number of large strings to extract from the managed heap (in addition to stack strings). Default: 0 (disabled).");
    Console.WriteLine("  --heap-histogram         Top N managed types by size to surface from the heap. Default: 0 (disabled).");
    Console.WriteLine("  --max-stack-frames       Maximum frames to capture per thread stack (for top stack threads). Default: 30.");
    Console.WriteLine("  --top-stack-threads      Number of threads to show stack traces for (ordered by lock count then id). Default: 5.");
    Console.WriteLine("  --json                   Write a JSON report to the given path.");
    Console.WriteLine("  --ai                     Enable local LLM reasoning (defaults to phi3:mini via Ollama).");
    Console.WriteLine("  --ai-model               Model name to pass to the local endpoint. Default: phi3:mini.");
    Console.WriteLine("  --ai-endpoint            HTTP endpoint for text generation (Ollama compatible). Default: http://localhost:11434/api/generate.");
    Console.WriteLine("  --ai-context-chars       Character budget for the LLM prompt to stay bounded. Default: 20000 (min 4000).");
    Console.WriteLine();
    Console.WriteLine("The tool runs entirely offline and uses heuristic reasoning");
    Console.WriteLine("to highlight potential crashes, locks, CPU spikes, memory pressure, deadlocks, hotspots, and captures strings when requested.");
    Console.WriteLine("AI mode adds a locally-hosted model (e.g., phi3:mini with Ollama) to summarize findings; no cloud calls are made.");
}

static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
