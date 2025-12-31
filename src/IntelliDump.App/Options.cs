namespace IntelliDump;

public sealed class Options
{
    public string DumpPath { get; }
    public int MaxStringsToCapture { get; }
    public int MaxStringLength { get; }

    private Options(string dumpPath, int maxStringsToCapture, int maxStringLength)
    {
        DumpPath = dumpPath;
        MaxStringsToCapture = maxStringsToCapture;
        MaxStringLength = maxStringLength;
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
        }

        if (maxStringsToCapture < 0)
        {
            maxStringsToCapture = 0;
        }

        if (maxStringLength <= 0)
        {
            maxStringLength = 65536;
        }

        return new Options(dumpPath, maxStringsToCapture, maxStringLength);
    }
}
