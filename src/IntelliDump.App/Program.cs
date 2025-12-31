using IntelliDump;
using IntelliDump.Diagnostics;
using IntelliDump.Output;
using IntelliDump.Reasoning;

try
{
    var options = Options.FromArgs(args);
    var loader = new DumpLoader();
    var snapshot = loader.Load(options);
    var issues = new LocalReasoner().Analyze(snapshot);

    var reporter = new ConsoleReporter();
    reporter.Render(snapshot, issues);
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

static void PrintHelp()
{
    Console.WriteLine("IntelliDump - offline intelligent IIS dump analyzer");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  intellidump <path-to-dump> [--strings <count>] [--max-string-length <chars>]");
    Console.WriteLine();
    Console.WriteLine("Example:");
    Console.WriteLine("  dotnet run --project src/IntelliDump.App ./iis-crash.dmp --strings 5 --max-string-length 120000");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --strings (-s)           Number of string values to extract from thread stacks (SQL, XML, etc.). Default: 0 (disabled).");
    Console.WriteLine("  --max-string-length      Maximum characters to emit per string to prevent runaway output. Default: 65536.");
    Console.WriteLine();
    Console.WriteLine("The tool runs entirely offline and uses heuristic reasoning");
    Console.WriteLine("to highlight potential crashes, locks, CPU spikes, memory pressure, and captures thread-affinitized strings when requested.");
}
