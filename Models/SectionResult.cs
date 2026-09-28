namespace SoaRuleExtractor.Models;

/// <summary>Per-section decision log entry; one is written for every section, including skipped ones.</summary>
public sealed record SectionResult(
    string SectionId, string HeadingPath, string Tier, string TierReason,
    int SignalHits, int RulesExtracted, int RulesWithIssues,
    string? Error, TimeSpan Duration);
