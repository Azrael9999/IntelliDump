using IntelliDump;

namespace IntelliDump.Tests;

public class OptionsTests
{
    [Fact]
    public void ParsesStringOptions()
    {
        var options = Options.FromArgs(new[]
        {
            "dump.dmp",
            "--strings", "3",
            "--max-string-length", "120000",
            "--heap-strings", "2",
            "--heap-histogram", "10",
            "--max-stack-frames", "50",
            "--top-stack-threads", "8",
            "--json", "report.json"
        });

        Assert.Equal("dump.dmp", options.DumpPath);
        Assert.Equal(3, options.MaxStringsToCapture);
        Assert.Equal(120000, options.MaxStringLength);
        Assert.Equal(2, options.HeapStringLimit);
        Assert.Equal(10, options.HeapHistogramCount);
        Assert.Equal(50, options.MaxStackFrames);
        Assert.Equal(8, options.TopStackThreads);
        Assert.Equal("report.json", options.JsonOutputPath);
    }

    [Fact]
    public void DefaultsToNoStringCapture()
    {
        var options = Options.FromArgs(new[] { "dump.dmp" });

        Assert.Equal(0, options.MaxStringsToCapture);
        Assert.Equal(65536, options.MaxStringLength);
        Assert.Equal(0, options.HeapStringLimit);
        Assert.Equal(0, options.HeapHistogramCount);
        Assert.Equal(30, options.MaxStackFrames);
        Assert.Equal(5, options.TopStackThreads);
        Assert.Null(options.JsonOutputPath);
    }
}
