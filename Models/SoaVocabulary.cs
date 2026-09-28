namespace SoaRuleExtractor.Models;

/// <summary>
/// The SoA grid vocabulary, projected from the study's own extracted SoA table
/// (see <see cref="Sources.SoaVocabularyLoader"/>): visits are the grid's timepoint
/// columns, procedures are its activity rows.
/// </summary>
public sealed record SoaVocabulary(
    IReadOnlyList<SoaVisit> Visits,
    IReadOnlyList<SoaProcedure> Procedures);

/// <summary>
/// One timepoint column already present in the source SoA table. Given to the agent as
/// context for recognizing day/period mentions — <see cref="VisitWindow"/> is not sent
/// to the agent, which must compute its own windows from the section text rather than
/// copy a window that may belong to a different arm or period.
/// </summary>
public sealed record SoaVisit(
    string VisitId,        // e.g. "D1_2h"
    string DayLabel,       // resolved day/period grouping used for matching: Period if the
                            // source populated it (e.g. "Day 1"), else the leading number
                            // parsed from StudyDay (e.g. "1" from "1 (Predose)"); see
                            // Sources.SoaVocabularyLoader — always non-empty
    string? Period,        // raw source field, kept for reference; often null
    string StudyDay,       // raw source field, e.g. "2h", "Pre-Dose", "1 (Predose)", "323\nEOS"
    string? VisitWindow);  // e.g. "(± 10)"; raw text, kept for reference only

/// <summary>An activity/procedure column in the SoA grid.</summary>
public sealed record SoaProcedure(
    string ProcedureId,   // e.g. "VITAL_SIGNS"
    string Name,          // cleaned display name, e.g. "Vital signs (incl. Temperature)"
    IReadOnlyList<string> Aliases); // extra recognizable terms derived from Name; may be empty
