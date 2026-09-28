using System.Text;
using System.Text.RegularExpressions;
using SoaRuleExtractor.Models;

namespace SoaRuleExtractor.Extraction;

/// <summary>Deterministic grounding and ID checks for extracted rules.</summary>
public sealed class RuleValidator(SoaVocabulary vocabulary)
{
    public const string QuoteNotFound = "quote_not_found";
    public const string UnknownProcedureId = "unknown_procedure_id";
    public const string UnmatchedProcedure = "unmatched_procedure";
    public const string UnknownPeriod = "unknown_period"; // Periods checked against each visit's DayLabel
    public const string NoTiming = "no_timing";
    public const string WindowInverted = "window_inverted";
    public const string BadGenerator = "bad_generator";

    private const string Unmatched = "UNMATCHED";

    private readonly HashSet<string> _procedureIds = vocabulary.Procedures.Select(p => p.ProcedureId).ToHashSet(StringComparer.Ordinal);
    private readonly HashSet<string> _dayLabels = vocabulary.Visits
        .Select(v => v.DayLabel)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns issue codes for a rule; empty means clean.</summary>
    public IReadOnlyList<string> Validate(TimepointRule rule, string sectionText)
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(rule.Quote)
            || !Normalize(sectionText).Contains(Normalize(rule.Quote), StringComparison.Ordinal))
            issues.Add(QuoteNotFound);

        if (rule.ProcedureId == Unmatched)
            issues.Add(UnmatchedProcedure);
        else if (!_procedureIds.Contains(rule.ProcedureId))
            issues.Add(UnknownProcedureId);

        if (rule.Periods?.Any(p => !_dayLabels.Contains(p)) == true)
            issues.Add(UnknownPeriod);

        if ((rule.Timepoints is null || rule.Timepoints.Count == 0) && rule.Generator is null)
            issues.Add(NoTiming);

        if (rule.Timepoints?.Any(t => t.WindowLowMinutes > t.WindowHighMinutes) == true)
            issues.Add(WindowInverted);

        if (rule.Generator is { } g && (g.IntervalMinutes <= 0 || g.EndMinutes < g.StartMinutes))
            issues.Add(BadGenerator);

        return issues;
    }

    /// <summary>
    /// Normalization for quote grounding: unicode compatibility form, unified dashes/quotes/±,
    /// markdown emphasis and table pipes removed, whitespace collapsed, lower-cased.
    /// </summary>
    public static string Normalize(string text)
    {
        var s = text.Normalize(NormalizationForm.FormKC);
        s = Regex.Replace(s, "[‐‑‒–—―−]", "-");
        s = Regex.Replace(s, "[‘’‚‛′]", "'");
        s = Regex.Replace(s, "[“”„‟″]", "\"");
        s = s.Replace("+/-", "±").Replace("+-", "±");
        s = Regex.Replace(s, @"[*_`#]", "");
        s = s.Replace('|', ' ');
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim().ToLowerInvariant();
    }
}
