using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using IntelliDump.Diagnostics;

namespace IntelliDump.Reasoning;

public sealed record AiSettings(string Model, string Endpoint, int ContextChars);

public sealed record AiReasoningResult(string? Summary, string? Problems, string? Error);
public sealed record AiAnswer(string? Answer, string? Error);

/// <summary>
/// Bridges heuristic findings to a local LLM endpoint (e.g., Ollama) to produce a narrative summary.
/// </summary>
public sealed class AiReasoner
{
    private readonly HttpClient _httpClient;

    public AiReasoner(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(40)
        };
    }

    public async Task<AiReasoningResult> AnalyzeAsync(
        DumpSnapshot snapshot,
        IReadOnlyList<AnalysisIssue> issues,
        AiSettings settings,
        CancellationToken cancellationToken)
    {
        var summaryPrompt = BuildSummaryPrompt(snapshot, issues, settings.ContextChars);
        var summaryResponse = await InvokeAsync(settings, summaryPrompt, cancellationToken);

        var problemsPrompt = BuildProblemPrompt(snapshot, issues, summaryResponse?.Response, settings.ContextChars);
        var problemsResponse = await InvokeAsync(settings, problemsPrompt, cancellationToken);

        if (summaryResponse?.Response is { Length: > 0 } summaryText ||
            problemsResponse?.Response is { Length: > 0 } problemsText)
        {
            return new AiReasoningResult(
                summaryResponse?.Response?.Trim(),
                problemsResponse?.Response?.Trim(),
                summaryResponse?.Error ?? problemsResponse?.Error);
        }

        return new AiReasoningResult(null, null, summaryResponse?.Error ?? problemsResponse?.Error ?? "AI endpoint returned an empty response.");
    }

    public async Task<AiAnswer> AnswerQuestionAsync(
        DumpSnapshot snapshot,
        IReadOnlyList<AnalysisIssue> issues,
        string question,
        AiSettings settings,
        CancellationToken cancellationToken)
    {
        var prompt = BuildQuestionPrompt(snapshot, issues, question, settings.ContextChars);
        var response = await InvokeAsync(settings, prompt, cancellationToken);

        if (response?.Response is { Length: > 0 } text)
        {
            return new AiAnswer(text.Trim(), response.Error);
        }

        return new AiAnswer(null, response?.Error ?? "AI endpoint returned an empty response.");
    }

    private static string BuildSummaryPrompt(DumpSnapshot snapshot, IReadOnlyList<AnalysisIssue> issues, int budget)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a .NET crash dump triage expert. Provide high-signal, actionable insights for an IIS/.NET dump.");
        sb.AppendLine("Return 3-6 bullet points covering root causes, hotspots, and next steps. Be concise and technical.");
        sb.AppendLine();

        sb.AppendLine($"Dump: {Path.GetFileName(snapshot.DumpPath)}");
        sb.AppendLine($"Runtime: {snapshot.RuntimeDescription ?? "unknown"}");
        sb.AppendLine($"Threads captured: {snapshot.Threads.Count}/{snapshot.TotalThreadCount} (deadlock candidates: {snapshot.DeadlockCandidates.Count})");
        sb.AppendLine($"GC: total {snapshot.Gc.TotalHeapBytes / (1024 * 1024):N0} MB, LOH {snapshot.Gc.LargeObjectHeapBytes / (1024 * 1024):N0} MB, pinned {snapshot.Gc.PinnedBytes / (1024 * 1024):N0} MB, mode={(snapshot.Gc.IsServerGc ? "server" : "workstation")}");
        sb.AppendLine($"Strings: {snapshot.UniqueStringCount} unique / {snapshot.TotalStringOccurrences} occurrences (stack {snapshot.StackStringOccurrences}, heap {snapshot.HeapStringOccurrences})");
        sb.AppendLine($"Modules shown: {Math.Min(20, snapshot.LoadedModules.Count)}/{snapshot.TotalModuleCount} covering {snapshot.ModuleCoverageShown * 100:N1}% of {snapshot.TotalModuleBytes / (1024 * 1024):N0} MB");
        sb.AppendLine();

        sb.AppendLine("Heuristic findings:");
        foreach (var issue in issues.Take(12))
        {
            sb.AppendLine($"- [{issue.Severity}] {issue.Title}: {Clamp(issue.Evidence, 280)} | Fix: {Clamp(issue.Recommendation, 200)}");
        }

        if (snapshot.DeadlockCandidates.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Deadlock objects:");
            foreach (var deadlock in snapshot.DeadlockCandidates.Take(5))
            {
                sb.AppendLine($"- obj 0x{deadlock.ObjectAddress:X}, owner={deadlock.OwnerThreadId?.ToString() ?? "unknown"}, waiting={deadlock.WaitingThreads}");
            }
        }

        if (snapshot.Strings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Notable strings (trimmed):");
            foreach (var s in snapshot.Strings.Take(5))
            {
                var owners = s.ThreadIds.Count == 0 ? "heap" : string.Join(",", s.ThreadIds.Take(3));
                sb.AppendLine($"- {s.Source}: count={s.Occurrences}, threads={owners}, text={Clamp(s.Value, 200)}");
            }
        }

        if (snapshot.LoadedModules.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Largest managed modules:");
            foreach (var module in snapshot.LoadedModules.OrderByDescending(m => m.Size).Take(5))
            {
                sb.AppendLine($"- {module.Name} ({module.Size / (1024 * 1024):N1} MB)");
            }
        }

        if (snapshot.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Data warnings:");
            foreach (var warning in snapshot.Warnings.Take(5))
            {
                sb.AppendLine($"- {warning.Category}: {warning.Message}");
            }
        }

        return ClampToBudget(sb, budget);
    }

    private static string BuildProblemPrompt(DumpSnapshot snapshot, IReadOnlyList<AnalysisIssue> issues, string? aiSummary, int budget)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Review the heuristic findings and iterate through them to pinpoint concrete problems.");
        sb.AppendLine("Produce a short list of the most plausible problems, each with: name, why it is likely (cite evidence), and the fix to validate.");
        sb.AppendLine("If data is insufficient, state that. Avoid generic advice. Keep 3-6 items.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(aiSummary))
        {
            sb.AppendLine("Previous AI summary to refine:");
            sb.AppendLine(aiSummary.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("Heuristic findings to iterate over:");
        foreach (var issue in issues.Take(15))
        {
            sb.AppendLine($"- [{issue.Severity}] {issue.Title}: {Clamp(issue.Evidence, 280)} | Fix: {Clamp(issue.Recommendation, 200)}");
        }

        if (snapshot.Threads.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Thread overview to validate blocking:");
            var running = snapshot.Threads.Count(t => t.State.Contains("Running", StringComparison.OrdinalIgnoreCase));
            var waiting = snapshot.Threads.Count(t => t.State.Contains("Wait", StringComparison.OrdinalIgnoreCase));
            sb.AppendLine($"Running={running}, Waiting={waiting}, Deadlocks={snapshot.DeadlockCandidates.Count}");
        }

        return ClampToBudget(sb, budget);
    }

    private static string BuildQuestionPrompt(DumpSnapshot snapshot, IReadOnlyList<AnalysisIssue> issues, string question, int budget)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a .NET dump analyst. Answer the user's question using the dump evidence below.");
        sb.AppendLine("If evidence is missing, state that plainly. Be concise and specific.");
        sb.AppendLine();
        sb.AppendLine($"Question: {question}");
        sb.AppendLine();

        sb.AppendLine($"Runtime: {snapshot.RuntimeDescription ?? "unknown"}, Threads {snapshot.Threads.Count}/{snapshot.TotalThreadCount}, Deadlocks {snapshot.DeadlockCandidates.Count}");
        sb.AppendLine($"GC: total {snapshot.Gc.TotalHeapBytes / (1024 * 1024):N0} MB, LOH {snapshot.Gc.LargeObjectHeapBytes / (1024 * 1024):N0} MB, pinned {snapshot.Gc.PinnedBytes / (1024 * 1024):N0} MB");
        sb.AppendLine($"Strings: {snapshot.UniqueStringCount} unique / {snapshot.TotalStringOccurrences} occurrences (stack {snapshot.StackStringOccurrences}, heap {snapshot.HeapStringOccurrences})");
        sb.AppendLine();

        sb.AppendLine("Heuristic findings:");
        foreach (var issue in issues.Take(12))
        {
            sb.AppendLine($"- [{issue.Severity}] {issue.Title}: {Clamp(issue.Evidence, 220)}");
        }

        if (snapshot.DeadlockCandidates.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Deadlock objects:");
            foreach (var deadlock in snapshot.DeadlockCandidates.Take(5))
            {
                sb.AppendLine($"- obj 0x{deadlock.ObjectAddress:X}, owner={deadlock.OwnerThreadId?.ToString() ?? "unknown"}, waiting={deadlock.WaitingThreads}");
            }
        }

        if (snapshot.Strings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Notable strings (trimmed):");
            foreach (var s in snapshot.Strings.Take(3))
            {
                var owners = s.ThreadIds.Count == 0 ? "heap" : string.Join(",", s.ThreadIds.Take(3));
                sb.AppendLine($"- {s.Source}: count={s.Occurrences}, threads={owners}, text={Clamp(s.Value, 160)}");
            }
        }

        return ClampToBudget(sb, budget);
    }

    private async Task<OllamaResponse?> InvokeAsync(AiSettings settings, string prompt, CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model = settings.Model,
            prompt,
            stream = false,
            options = new
            {
                temperature = 0.1,
                num_predict = 512
            }
        };

        using var response = await _httpClient.PostAsJsonAsync(settings.Endpoint, requestBody, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new OllamaResponse
            {
                Error = $"AI endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}. Ensure the model is pulled locally (e.g., `ollama run {settings.Model}`)."
            };
        }

        var payload = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken: cancellationToken);
        if (payload is not null)
        {
            return payload;
        }

        return new OllamaResponse { Error = "AI endpoint returned an empty response." };
    }

    private static string ClampToBudget(StringBuilder sb, int budget)
    {
        var prompt = sb.ToString();
        if (prompt.Length <= budget)
        {
            return prompt;
        }

        return prompt[..budget];
    }

    private static string Clamp(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    private sealed class OllamaResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; init; }

        [JsonIgnore]
        public string? Error { get; init; }
    }
}
