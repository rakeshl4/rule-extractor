using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using SoaRuleExtractor.Models;

namespace SoaRuleExtractor.Extraction;

/// <summary>Raw and parsed output of one agent call.</summary>
public sealed record AgentExtraction(TimepointRuleSet RuleSet, string RawJson);

/// <summary>
/// Single MAF agent that extracts <see cref="TimepointRule"/>s from one section.
/// Uses a fresh session per call, so it is safe to call in parallel.
/// </summary>
public sealed class TimepointRuleAgent
{
    public const string Instructions = """
        You extract TIMEPOINT RULES from one section of a clinical study protocol.

        A timepoint rule states that an activity is performed at one or more specific
        times relative to an anchor event (usually dosing), possibly repeated, with
        time windows, or with replicates.

        Examples:
        - "PK samples are collected pre-dose (within 30 min) and at 2 h (±10 min),
          4 h and 5 h (±15 min) post-dose on Day 1."
          -> procedure PHARMACOKINETICS, periods ["Day 1"], anchor dose,
             timepoints pre-dose(0, -30..0), 2h(120, -10..10), 4h(240, -15..15),
             5h(300, -15..15)
        - "Vital signs every hour for the first 4 hours after dosing (±5 min)."
          -> use generator start 60, end 240, interval 60, tolerance 5. Do NOT list the
             individual timepoints yourself.
        - "12-lead ECGs in triplicate approximately 2 minutes apart at pre-dose and 2 h."
          -> replicates count 3, spacing 2; timepoints pre-dose and 2h.
        - "An additional PK sample is taken at 24 h on Day 1." -> mode "add".

        Rules:
        - Convert ALL times to minutes relative to the anchor. Before the anchor is negative.
        - "Pre-dose" has NominalMinutes 0. Only set window values that are stated;
          otherwise leave them null. Never invent windows.
        - Map activities to the vocabulary's ProcedureId using each procedure's Name and
          Aliases. If an activity is not in the vocabulary, use ProcedureId "UNMATCHED"
          and put the raw text in UnmatchedProcedureText.
        - Map day/period mentions ("Day 1", "Day 8", or a plain day number like "1" if
          that is the convention the vocabulary's Visits use) to the Day values shown
          there. If the rule applies to every day/period the activity occurs, set
          Periods to null.
        - The vocabulary's Visits list shows existing timepoint columns for context only
          (which days and study-day labels are already tracked in the SoA table). Do
          not copy any window from it — compute NominalMinutes and windows only from what
          this section's text states; a different arm or period may use different windows.
        - Mode is "replace" unless the text says additional/extra/also, then "add".
        - Quote must be copied VERBATIM from the section text, the shortest span that
          fully states the rule.
        - One rule per activity per distinct timing statement.
        - Ignore conditional repeats ("if abnormal, repeat") — they are a different
          rule type and out of scope here.
        - Ignore rationale, background, and statistical methodology.
        - If the section contains no timepoint rules, return an empty Rules list.
        """;

    private readonly AIAgent _agent;
    private readonly string _vocabularyJson;

    public TimepointRuleAgent(ChatClient chatClient, SoaVocabulary vocabulary, float? temperature)
    {
        _agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "TimepointRuleExtractor",
            ChatOptions = new ChatOptions { Instructions = Instructions, Temperature = temperature },
        });

        // Compact vocabulary: only what the model needs for mapping. A single resolved
        // "Day" is sent rather than the source's separate Period/StudyDay fields, since
        // which one actually carries the day grouping varies by document (see
        // SoaVisit.DayLabel). VisitWindow is deliberately omitted — see the "do not copy"
        // instruction above.
        _vocabularyJson = JsonSerializer.Serialize(new
        {
            Visits = vocabulary.Visits.Select(v => new { v.VisitId, Day = v.DayLabel }),
            Procedures = vocabulary.Procedures.Select(p => new { p.ProcedureId, p.Name, p.Aliases }),
        }, new JsonSerializerOptions(Json.File) { WriteIndented = false });
    }

    public async Task<AgentExtraction> ExtractAsync(Section section, CancellationToken ct)
    {
        var message = $"""
            SoA vocabulary:
            {_vocabularyJson}

            Heading path: {section.HeadingPath}

            Section text:
            {section.Markdown}
            """;

        AgentResponse<TimepointRuleSet> response = await _agent.RunAsync<TimepointRuleSet>(
            message, session: null, serializerOptions: Json.Agent, options: null, cancellationToken: ct);

        return new AgentExtraction(response.Result ?? new TimepointRuleSet([]), response.Text);
    }
}
