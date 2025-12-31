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
            $"Runtime: {snapshot.RuntimeDescription} | Threads: {snapshot.Threads.Count} | Warnings: {snapshot.Warnings.Count}";

        ThreadsSummary =
            $"Finalizers: {snapshot.FinalizerThreads.Count} | Deadlocks: {snapshot.DeadlockCandidates.Count} | Stack strings: {snapshot.Strings.Count}";

        GcSummary =
            $"Total heap: {snapshot.Gc.TotalHeapBytes / (1024 * 1024):N0} MB | LOH: {snapshot.Gc.LargeObjectHeapBytes / (1024 * 1024):N0} MB | Pinned: {snapshot.Gc.PinnedBytes / (1024 * 1024):N0} MB";
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
