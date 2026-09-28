namespace SoaRuleExtractor.Models;

/// <summary>
/// An activity performed at one or more timepoints relative to an anchor event,
/// possibly with windows, a generator ("every N h for M h") or replicates.
/// This is the LLM structured-output schema; optional values are nullable, never omitted.
/// </summary>
public sealed record TimepointRule(
    string ProcedureId,                // vocabulary Procedure.ProcedureId, or "UNMATCHED"
    string? UnmatchedProcedureText,     // raw mention if ProcedureId == "UNMATCHED"
    IReadOnlyList<string>? Periods,     // e.g. ["Day 1"]; null = every period the activity occurs
    string Anchor,                      // "dose" | "visit_start" | "end_of_infusion" | "other"
    IReadOnlyList<Timepoint> Timepoints,
    TimepointGenerator? Generator,      // for "every N h for M h"; never enumerated
    Replicates? Replicates,             // for "in triplicate ~2 min apart"
    string Mode,                        // "replace" | "add"
    string Quote,                       // VERBATIM text from the section stating this rule
    string? Notes);

/// <summary>A single nominal time relative to the anchor, with an optional window.</summary>
public sealed record Timepoint(
    string Label,                      // "pre-dose", "2h", "4h"
    int NominalMinutes,                // offset from anchor; pre-dose = 0
    int? WindowLowMinutes,             // e.g. -30; null if not stated
    int? WindowHighMinutes);           // e.g. 15; null if not stated

/// <summary>A repeating schedule, e.g. every 60 min from 60 to 240 min.</summary>
public sealed record TimepointGenerator(
    int StartMinutes, int EndMinutes, int IntervalMinutes, int? ToleranceMinutes);

/// <summary>Repeated measurements at each timepoint, e.g. triplicate 2 min apart.</summary>
public sealed record Replicates(int Count, int SpacingMinutes);

/// <summary>Top-level structured output returned by the agent for one section.</summary>
public sealed record TimepointRuleSet(IReadOnlyList<TimepointRule> Rules);

/// <summary>A timepoint rule wrapped with pipeline metadata (not part of the LLM schema).</summary>
public sealed record ExtractedRule(
    string RuleId,                     // e.g. "TP-8.4.2-01"
    string SectionId,
    string HeadingPath,
    TimepointRule Rule,
    IReadOnlyList<string> ValidationIssues);
