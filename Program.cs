using System.ClientModel.Primitives;
using System.Globalization;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using SoaRuleExtractor;
using SoaRuleExtractor.Extraction;
using SoaRuleExtractor.Models;
using SoaRuleExtractor.Sources;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables() // e.g. AzureOpenAI__Endpoint, Extraction__MaxParallelism
    .Build();

var sampleDir = Path.Combine(AppContext.BaseDirectory, "SampleData");
var vocabulary = await SoaVocabularyLoader.FromFileAsync(Path.Combine(sampleDir, "soa-vocabulary.json"));

if (args.Contains("--selftest"))
    return SelfTest.Run(vocabulary);
if (args.Contains("--schema"))
{
    // The response-format schema MAF sends for TimepointRuleSet (handy when editing the records).
    Console.WriteLine(Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema<TimepointRuleSet>(Json.Agent).Schema);
    return 0;
}
var inspectIndex = Array.IndexOf(args, "--inspect-vocabulary");
if (inspectIndex >= 0)
{
    // No LLM call: a quick way to eyeball how a real extraction document's names and
    // day labels get cleaned up before spending a run against it.
    var inspectPath = inspectIndex + 1 < args.Length ? args[inspectIndex + 1] : Path.Combine(sampleDir, "soa-vocabulary.json");
    var inspected = await SoaVocabularyLoader.FromFileAsync(inspectPath);
    Console.WriteLine($"Visits ({inspected.Visits.Count}):");
    foreach (var v in inspected.Visits)
        Console.WriteLine($"  {v.VisitId,-12} day='{v.DayLabel}'  window={v.VisitWindow ?? "(none)"}");
    Console.WriteLine($"Procedures ({inspected.Procedures.Count}):");
    foreach (var pr in inspected.Procedures)
        Console.WriteLine($"  {pr.ProcedureId,-24} name='{pr.Name}'  aliases=[{string.Join(", ", pr.Aliases)}]");
    return 0;
}

// Works unchanged for an OpenAI model deployed via Azure AI Foundry: a Foundry resource
// (kind "AIServices") still exposes the classic Azure OpenAI deployments REST surface, so
// Endpoint is the Foundry resource endpoint (https://<resource>.services.ai.azure.com or
// https://<resource>.cognitiveservices.azure.com — NOT the project-level /api/projects/... URL)
// and Deployment is the model deployment name shown in the Foundry portal. Auth is API-key
// based when AzureOpenAI:ApiKey is set (or the AZURE_OPENAI_API_KEY env var), which is
// simplest for a resource-level key from the portal; otherwise it falls back to `az login`
// (AzureCliCredential), which needs the "Cognitive Services OpenAI User" role (or higher)
// on that resource.
var endpoint = config["AzureOpenAI:Endpoint"];
var deployment = config["AzureOpenAI:Deployment"];
var apiKey = config["AzureOpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(deployment))
{
    Console.Error.WriteLine("Set AzureOpenAI:Endpoint and AzureOpenAI:Deployment in appsettings.json or env (AzureOpenAI__Endpoint, AzureOpenAI__Deployment). For Azure AI Foundry, use the Foundry resource endpoint and the model's deployment name.");
    return 1;
}
var maxParallelism = int.TryParse(config["Extraction:MaxParallelism"], out var p) && p > 0 ? p : 4;
// Empty value omits temperature (for deployments that reject it, e.g. reasoning models).
var temperatureText = config["Extraction:Temperature"];
float? temperature = string.IsNullOrWhiteSpace(temperatureText) ? null : float.Parse(temperatureText, CultureInfo.InvariantCulture);
var outputDir = Path.GetFullPath(config["Extraction:OutputDir"] ?? "Output");

// Retries are handled by ExtractionRunner (so they are visible and counted), so disable the SDK's own.
var clientOptions = new AzureOpenAIClientOptions { RetryPolicy = new ClientRetryPolicy(maxRetries: 0) };
var azureClient = string.IsNullOrWhiteSpace(apiKey)
    ? new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential(), clientOptions)
    : new AzureOpenAIClient(new Uri(endpoint), new System.ClientModel.ApiKeyCredential(apiKey), clientOptions);
var chatClient = azureClient.GetChatClient(deployment);

var runner = new ExtractionRunner(
    new JsonFileSectionSource(Path.Combine(sampleDir, "sections.json")),
    new SectionTriage(vocabulary),
    new TimepointRuleAgent(chatClient, vocabulary, temperature),
    new RuleValidator(vocabulary),
    maxParallelism,
    outputDir);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"Extracting timepoint rules with '{deployment}' (parallelism {maxParallelism})");
var summary = await runner.RunAsync(cts.Token);

Console.WriteLine();
Console.WriteLine($"Sections: {summary.SectionsTotal} total, {summary.SectionsSkipped} skipped, " +
                  $"{summary.SectionsExtracted} extracted, {summary.SectionsFailed} failed");
Console.WriteLine($"Rules:    {summary.RulesTotal} total");
foreach (var (code, count) in summary.RulesWithIssuesByCode)
    Console.WriteLine($"          {count} x {code}");
if (summary.LikelyMissedSectionIds.Count > 0)
    Console.WriteLine($"Likely missed (>= 3 signal hits, 0 rules): {string.Join(", ", summary.LikelyMissedSectionIds)}");
Console.WriteLine($"Output:   {outputDir}");
return summary.SectionsFailed > 0 ? 2 : 0;

/// <summary>Deterministic checks for triage and validation (no LLM). Run with --selftest.</summary>
static class SelfTest
{
    public static int Run(SoaVocabulary vocabulary)
    {
        var failures = 0;
        void Check(bool condition, string name)
        {
            Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}");
            if (!condition) failures++;
        }

        var triage = new SectionTriage(vocabulary);
        Check(triage.Assess(new("1.2", "1 Intro > 1.2 List of Abbreviations", "PK = pharmacokinetics")).Tier == SectionTriage.Skip,
            "abbreviations with 1 signal hit are skipped");
        Check(triage.Assess(new("10.1", "10 Regulatory Considerations", "PK samples pre-dose and 2 h post-dose")).Tier == SectionTriage.Extract,
            "skip heading promoted with >= 2 signal hits");
        // "vital signs" alias, every 2, +/-5, "2 hours", "5 min", triplicate
        Check(triage.CountSignalHits("Vital signs every 2 hours (±5 min), in triplicate") == 6,
            "signal hits counted per pattern match");

        var validator = new RuleValidator(vocabulary);
        var text = "ECGs at 2 h (±10 min) – recorded in “triplicate”.";
        TimepointRule Rule(string procedureId = "ECG_12_LEAD", string quote = "at 2 h (±10 min) - recorded in \"triplicate\"",
            IReadOnlyList<string>? periods = null, int? low = -10, int? high = 10, TimepointGenerator? gen = null, bool noTimepoints = false) =>
            new(procedureId, null, periods, "dose",
                noTimepoints ? [] : [new Timepoint("2h", 120, low, high)], gen, null, "replace", quote, null);

        Check(validator.Validate(Rule(), text).Count == 0, "clean rule has no issues (quote normalization)");
        Check(validator.Validate(Rule(quote: "at 3 h"), text).Contains(RuleValidator.QuoteNotFound), "quote_not_found");
        Check(validator.Validate(Rule(procedureId: "XYZ"), text).Contains(RuleValidator.UnknownProcedureId), "unknown_procedure_id");
        Check(validator.Validate(Rule(procedureId: "UNMATCHED"), text).Contains(RuleValidator.UnmatchedProcedure), "unmatched_procedure");
        Check(validator.Validate(Rule(periods: ["Day 99"]), text).Contains(RuleValidator.UnknownPeriod), "unknown_period");
        Check(validator.Validate(Rule(noTimepoints: true), text).Contains(RuleValidator.NoTiming), "no_timing");
        Check(!validator.Validate(Rule(noTimepoints: true, gen: new(60, 240, 60, 5)), text).Contains(RuleValidator.NoTiming), "generator counts as timing");
        Check(validator.Validate(Rule(low: 10, high: -10), text).Contains(RuleValidator.WindowInverted), "window_inverted");
        Check(validator.Validate(Rule(gen: new(240, 60, 60, null)), text).Contains(RuleValidator.BadGenerator), "bad_generator");

        Console.WriteLine(failures == 0 ? "All self-tests passed." : $"{failures} self-test(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
