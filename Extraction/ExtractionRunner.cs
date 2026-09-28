using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using SoaRuleExtractor.Models;
using SoaRuleExtractor.Sources;

namespace SoaRuleExtractor.Extraction;

/// <summary>Aggregate counts written to run-summary.json.</summary>
public sealed record RunSummary(
    int SectionsTotal, int SectionsSkipped, int SectionsExtracted, int SectionsFailed,
    int RulesTotal, IReadOnlyDictionary<string, int> RulesWithIssuesByCode,
    IReadOnlyList<string> LikelyMissedSectionIds);

/// <summary>Iterates sections, triages, extracts with bounded parallelism, validates and writes outputs.</summary>
public sealed class ExtractionRunner(
    ISectionSource source,
    SectionTriage triage,
    TimepointRuleAgent agent,
    RuleValidator validator,
    int maxParallelism,
    string outputDir)
{
    private const int MaxAttempts = 3;
    private const int LikelyMissedSignalHits = 3;

    public async Task<RunSummary> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(outputDir, "raw"));
        using var gate = new SemaphoreSlim(maxParallelism);

        var tasks = new List<Task<(SectionResult Result, IReadOnlyList<ExtractedRule> Rules)>>();
        await foreach (var section in source.GetSectionsAsync(ct))
            tasks.Add(ProcessSectionAsync(section, gate, ct));

        var outcomes = await Task.WhenAll(tasks); // preserves source order
        var sectionLog = outcomes.Select(o => o.Result).ToList();
        var rules = outcomes.SelectMany(o => o.Rules).ToList();

        var summary = new RunSummary(
            SectionsTotal: sectionLog.Count,
            SectionsSkipped: sectionLog.Count(s => s.Tier == SectionTriage.Skip),
            SectionsExtracted: sectionLog.Count(s => s.Tier == SectionTriage.Extract && s.Error is null),
            SectionsFailed: sectionLog.Count(s => s.Error is not null),
            RulesTotal: rules.Count,
            RulesWithIssuesByCode: rules.SelectMany(r => r.ValidationIssues)
                .GroupBy(c => c).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            LikelyMissedSectionIds: sectionLog
                .Where(s => s.Error is null && s.SignalHits >= LikelyMissedSignalHits && s.RulesExtracted == 0)
                .Select(s => s.SectionId).ToList());

        await WriteJsonAsync("rules.json", rules, ct);
        await WriteJsonAsync("section-log.json", sectionLog, ct);
        await WriteJsonAsync("run-summary.json", summary, ct);
        return summary;
    }

    private async Task<(SectionResult, IReadOnlyList<ExtractedRule>)> ProcessSectionAsync(
        Section section, SemaphoreSlim gate, CancellationToken ct)
    {
        var (tier, reason, hits) = triage.Assess(section);
        if (tier == SectionTriage.Skip)
            return (new SectionResult(section.SectionId, section.HeadingPath, tier, reason, hits, 0, 0, null, TimeSpan.Zero), []);

        await gate.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            var rules = await ExtractAndValidateAsync(section, "", ct);

            // Ungrounded quote: retry the whole section once, keep whichever attempt grounds better.
            if (rules.Any(r => r.Issues.Contains(RuleValidator.QuoteNotFound)))
            {
                Console.WriteLine($"  [{section.SectionId}] quote_not_found, retrying once");
                var retry = await ExtractAndValidateAsync(section, ".retry", ct);
                if (CountUngrounded(retry) < CountUngrounded(rules))
                    rules = retry;
            }

            var extracted = rules.Select((r, i) => new ExtractedRule(
                $"TP-{section.SectionId}-{i + 1:00}", section.SectionId, section.HeadingPath, r.Rule, r.Issues)).ToList();

            Console.WriteLine($"  [{section.SectionId}] {extracted.Count} rule(s) in {sw.Elapsed.TotalSeconds:0.0}s");
            return (new SectionResult(section.SectionId, section.HeadingPath, tier, reason, hits,
                extracted.Count, extracted.Count(r => r.ValidationIssues.Count > 0), null, sw.Elapsed), extracted);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine($"  [{section.SectionId}] FAILED: {ex.Message}");
            return (new SectionResult(section.SectionId, section.HeadingPath, tier, reason, hits, 0, 0,
                $"{ex.GetType().Name}: {ex.Message}", sw.Elapsed), []);
        }
        finally
        {
            gate.Release();
        }
    }

    private static int CountUngrounded(List<(TimepointRule Rule, IReadOnlyList<string> Issues)> rules) =>
        rules.Count(r => r.Issues.Contains(RuleValidator.QuoteNotFound));

    private async Task<List<(TimepointRule Rule, IReadOnlyList<string> Issues)>> ExtractAndValidateAsync(
        Section section, string rawSuffix, CancellationToken ct)
    {
        var extraction = await WithTransientRetryAsync(() => agent.ExtractAsync(section, ct), section.SectionId, ct);
        await File.WriteAllTextAsync(RawPath(section.SectionId, rawSuffix), extraction.RawJson, ct);
        return extraction.RuleSet.Rules.Select(r => (r, validator.Validate(r, section.Markdown))).ToList();
    }

    /// <summary>Retries HTTP 429/5xx and network errors with exponential backoff (1s, 2s, ...).</summary>
    private static async Task<T> WithTransientRetryAsync<T>(Func<Task<T>> action, string sectionId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                Console.WriteLine($"  [{sectionId}] transient error (attempt {attempt}/{MaxAttempts}), retrying in {delay.TotalSeconds}s: {ex.Message}");
                await Task.Delay(delay, ct);
            }
        }
    }

    private static bool IsTransient(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is ClientResultException { Status: 429 or >= 500 }) return true;
            if (e is HttpRequestException or TimeoutException) return true;
        }
        return false;
    }

    private string RawPath(string sectionId, string suffix)
    {
        var safeId = string.Concat(sectionId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(outputDir, "raw", $"{safeId}{suffix}.json");
    }

    private async Task WriteJsonAsync<T>(string fileName, T value, CancellationToken ct)
    {
        await using var stream = File.Create(Path.Combine(outputDir, fileName));
        await JsonSerializer.SerializeAsync(stream, value, Json.File, ct);
    }
}
