using System.Text.RegularExpressions;
using SoaRuleExtractor.Models;

namespace SoaRuleExtractor.Extraction;

/// <summary>Deterministic (no LLM) decision on whether a section is worth sending to the agent.</summary>
public sealed class SectionTriage
{
    public const string Skip = "Skip";
    public const string Extract = "Extract";
    public const int PromoteThreshold = 2;

    private static readonly string[] SkipHeadingKeywords =
    [
        "abbreviation", "reference", "signature", "table of contents", "statistic",
        "sample size", "publication", "ethic", "regulatory",
    ];

    private static readonly string[] TimingPatterns =
    [
        @"pre-?dose", @"post-?dose", @"±\s*\d+", @"\b\d+\s*(h|hr|hrs|hours|min|minutes)\b",
        @"every\s+\d+", @"triplicate|duplicate",
    ];

    private readonly IReadOnlyList<Regex> _signals;

    public SectionTriage(SoaVocabulary vocabulary)
    {
        var terms = vocabulary.Procedures
            .SelectMany(p => p.Aliases.Prepend(p.Name))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => $@"\b{Regex.Escape(t)}\b");

        _signals = terms.Concat(TimingPatterns)
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
    }

    /// <summary>Total matches of all signal patterns in the section body.</summary>
    public int CountSignalHits(string markdown) => _signals.Sum(r => r.Matches(markdown).Count);

    public (string Tier, string Reason, int SignalHits) Assess(Section section)
    {
        var hits = CountSignalHits(section.Markdown);
        var keyword = SkipHeadingKeywords.FirstOrDefault(
            k => section.HeadingPath.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (keyword is null)
            return (Extract, "default", hits);
        if (hits >= PromoteThreshold)
            return (Extract, $"heading matches '{keyword}' but promoted: {hits} signal hits", hits);
        return (Skip, $"heading matches '{keyword}' ({hits} signal hits)", hits);
    }
}
