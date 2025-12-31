namespace IntelliDump;

public sealed class Options
{
    public string DumpPath { get; }
    public int MaxStringsToCapture { get; }
    public int MaxStringLength { get; }
    public int HeapStringLimit { get; }
    public int HeapHistogramCount { get; }
    public string? JsonOutputPath { get; }
    public int MaxStackFrames { get; }
    public int TopStackThreads { get; }

    public Options(
        string dumpPath,
        int maxStringsToCapture,
        int maxStringLength,
        int heapStringLimit,
        int heapHistogramCount,
        int maxStackFrames,
        int topStackThreads,
        string? jsonOutputPath)
    {
        DumpPath = dumpPath;
        MaxStringsToCapture = maxStringsToCapture;
        MaxStringLength = maxStringLength;
        HeapStringLimit = heapStringLimit;
        HeapHistogramCount = heapHistogramCount;
        MaxStackFrames = maxStackFrames;
        TopStackThreads = topStackThreads;
        JsonOutputPath = jsonOutputPath;
    }

    public static Options FromArgs(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            throw new ArgumentException("help");
        }

        var dumpPath = args[0];
        int maxStringsToCapture = 0;
        int maxStringLength = 65536; // 64KB default cap for gigantic SQL/XML payloads
        int heapStringLimit = 0;
        int heapHistogramCount = 0;
        int maxStackFrames = 30;
        int topStackThreads = 5;
        string? jsonOutputPath = null;

        for (var i = 1; i < args.Length; i++)
        {
            var current = args[i];
            if (current is "--strings" or "-s")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out maxStringsToCapture))
                {
                    throw new ArgumentException("Invalid value for --strings. Provide an integer count.");
                }

                i++;
            }
            else if (current is "--max-string-length")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out maxStringLength))
                {
                    throw new ArgumentException("Invalid value for --max-string-length. Provide an integer length in characters.");
                }

                i++;
            }
            else if (current is "--heap-strings")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out heapStringLimit))
                {
                    throw new ArgumentException("Invalid value for --heap-strings. Provide an integer count.");
                }

                i++;
            }
            else if (current is "--heap-histogram")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out heapHistogramCount))
                {
                    throw new ArgumentException("Invalid value for --heap-histogram. Provide an integer count.");
                }

                i++;
            }
            else if (current is "--max-stack-frames")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out maxStackFrames))
                {
                    throw new ArgumentException("Invalid value for --max-stack-frames. Provide an integer count.");
                }

                i++;
            }
            else if (current is "--top-stack-threads")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out topStackThreads))
                {
                    throw new ArgumentException("Invalid value for --top-stack-threads. Provide an integer count.");
                }

                i++;
            }
            else if (current is "--json")
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("Invalid value for --json. Provide a file path.");
                }

                jsonOutputPath = args[i + 1];
                i++;
            }
        }

        if (maxStringsToCapture < 0)
        {
            maxStringsToCapture = 0;
        }

        if (maxStringLength <= 0)
        {
            maxStringLength = 65536;
        }

        if (heapStringLimit < 0)
        {
            heapStringLimit = 0;
        }

        if (heapHistogramCount < 0)
        {
            heapHistogramCount = 0;
        }

        if (maxStackFrames <= 0)
        {
            maxStackFrames = 30;
        }

        if (topStackThreads <= 0)
        {
            topStackThreads = 5;
        }

        return new Options(
            dumpPath,
            maxStringsToCapture,
            maxStringLength,
            heapStringLimit,
            heapHistogramCount,
            maxStackFrames,
            topStackThreads,
            jsonOutputPath);
    }
}
