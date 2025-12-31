namespace IntelliDump.Reasoning;

public sealed record AnalysisIssue(
    string Title,
    IssueSeverity Severity,
    string Evidence,
    string Recommendation);
