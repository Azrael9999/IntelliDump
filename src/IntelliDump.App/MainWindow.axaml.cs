using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using IntelliDump.Diagnostics;
using IntelliDump.Output;
using IntelliDump.Reasoning;
using IntelliDump.Reporting;

namespace IntelliDump;

public partial class MainWindow : Window
{
    private readonly DumpLoader _loader = new();
    private readonly LocalReasoner _reasoner = new();
    private DumpSnapshot? _lastSnapshot;
    private IReadOnlyList<AnalysisIssue>? _lastIssues;

    public ObservableCollection<FindingViewModel> Findings { get; } = new();

    public string DumpPath { get; set; } = string.Empty;
    public string Status { get; set; } = "Pick a dump file to begin.";
    public string HighlightSummary { get; set; } = string.Empty;
    public string ThreadsSummary { get; set; } = string.Empty;
    public string GcSummary { get; set; } = string.Empty;
    public string StringsSummary { get; set; } = string.Empty;
    public string CoverageSummary { get; set; } = string.Empty;
    public string DeadlockSummary { get; set; } = string.Empty;
    public string WarningsSummary { get; set; } = string.Empty;
    public string PdfPath { get; set; } = string.Empty;
    public int StackStrings { get; set; } = 5;
    public int HeapStrings { get; set; } = 5;
    public int MaxStringLength { get; set; } = 120000;
    public int HeapHistogram { get; set; } = 15;
    public int TopStackThreads { get; set; } = 5;
    public int MaxStackFrames { get; set; } = 30;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private async void OpenDump_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filters = { new FileDialogFilter { Name = "Dump files", Extensions = { "dmp" } } }
        };
        var result = await dialog.ShowAsync(this);
        if (result is { Length: > 0 })
        {
            DumpPath = result[0];
            Status = $"Selected: {DumpPath}";
            RefreshBindings();
        }
    }

    private async void Analyze_Click(object? sender, RoutedEventArgs e)
    {
        await RunAnalysisAsync();
    }

    private async void ExportPdf_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastSnapshot is null || _lastIssues is null)
        {
            Status = "Run an analysis before exporting.";
            RefreshBindings();
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filters = { new FileDialogFilter { Name = "PDF", Extensions = { "pdf" } } },
            InitialFileName = "intellidump-report.pdf"
        };

        var path = await dialog.ShowAsync(this);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            PdfReportBuilder.Build(path, _lastSnapshot, _lastIssues);
            PdfPath = $"PDF saved: {path}";
            Status = "PDF export complete.";
        }
        catch (Exception ex)
        {
            Status = $"PDF export failed: {ex.Message}";
        }

        RefreshBindings();
    }

    private async Task RunAnalysisAsync()
    {
        if (string.IsNullOrWhiteSpace(DumpPath) || !File.Exists(DumpPath))
        {
            Status = "Select a valid dump file.";
            RefreshBindings();
            return;
        }

        Status = "Analyzing dump...";
        RefreshBindings();

        try
        {
            var options = new Options(
                DumpPath,
                StackStrings,
                MaxStringLength,
                HeapStrings,
                HeapHistogram,
                MaxStackFrames,
                TopStackThreads,
                null);

            DumpSnapshot snapshot = await Task.Run(() => _loader.Load(options));
            var issues = _reasoner.Analyze(snapshot);

            _lastSnapshot = snapshot;
            _lastIssues = issues;
            RenderSummaries(snapshot, issues);
            Status = $"Analysis finished: {issues.Count} findings.";
        }
        catch (Exception ex)
        {
            Status = $"Analysis failed: {ex.Message}";
        }

        RefreshBindings();
    }

    private void RenderSummaries(DumpSnapshot snapshot, IReadOnlyList<AnalysisIssue> issues)
    {
        Findings.Clear();
        foreach (var issue in issues)
        {
            Findings.Add(new FindingViewModel(issue));
        }

        HighlightSummary =
            $"Runtime: {snapshot.RuntimeDescription} | Threads shown: {snapshot.Threads.Count}/{snapshot.TotalThreadCount} | Warnings: {snapshot.Warnings.Count}";

        ThreadsSummary =
            $"Running: {snapshot.Threads.Count(t => t.State.Contains(\"Running\", StringComparison.OrdinalIgnoreCase))} | Waiting: {snapshot.Threads.Count(t => t.State.Contains(\"Wait\", StringComparison.OrdinalIgnoreCase))} | Finalizers: {snapshot.FinalizerThreads.Count} | Deadlocks: {snapshot.DeadlockCandidates.Count}";

        GcSummary =
            $"Total heap: {snapshot.Gc.TotalHeapBytes / (1024 * 1024):N0} MB | LOH: {snapshot.Gc.LargeObjectHeapBytes / (1024 * 1024):N0} MB | Pinned: {snapshot.Gc.PinnedBytes / (1024 * 1024):N0} MB";

        StringsSummary =
            $"Strings: {snapshot.UniqueStringCount} unique / {snapshot.TotalStringOccurrences} occurrences (stack {snapshot.StackStringOccurrences}, heap {snapshot.HeapStringOccurrences}) | Captured strings: {snapshot.Strings.Count}";

        CoverageSummary =
            $"Heap types: {snapshot.HeapTypes.Count}/{snapshot.TotalHeapTypeCount} covering {snapshot.HeapHistogramCoverage * 100:N1}% | Modules: {Math.Min(20, snapshot.LoadedModules.Count)}/{snapshot.TotalModuleCount} covering {snapshot.ModuleCoverageShown * 100:N1}% of {snapshot.TotalModuleBytes / (1024 * 1024):N0} MB";

        if (snapshot.DeadlockCandidates.Count > 0)
        {
            var first = snapshot.DeadlockCandidates.First();
            DeadlockSummary = $"Deadlock candidates detected: {snapshot.DeadlockCandidates.Count} (e.g., object 0x{first.ObjectAddress:X} owner={first.OwnerThreadId?.ToString() ?? "unknown"} waiting={first.WaitingThreads})";
        }
        else
        {
            DeadlockSummary = "No deadlock candidates detected.";
        }

        WarningsSummary = snapshot.Warnings.Count == 0
            ? "No data warnings."
            : string.Join(" | ", snapshot.Warnings
                .GroupBy(w => w.Category)
                .Select(g => $"{g.Key}: {g.Count()}"));
    }

    private void RefreshBindings()
    {
        this.DataContext = null;
        this.DataContext = this;
    }
}

public sealed record FindingViewModel(string Title, string Evidence, string Recommendation, string SeverityColor)
{
    public FindingViewModel(AnalysisIssue issue)
        : this(issue.Title, issue.Evidence, issue.Recommendation, ColorFromSeverity(issue.Severity))
    {
    }

    private static string ColorFromSeverity(IssueSeverity severity) =>
        severity switch
        {
            IssueSeverity.Critical => "#fecdd3",
            IssueSeverity.Warning => "#fef9c3",
            _ => "#dcfce7"
        };
}
