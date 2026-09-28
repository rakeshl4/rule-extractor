using System.Text.Json;
using System.Text.RegularExpressions;
using SoaRuleExtractor.Models;

namespace SoaRuleExtractor.Sources;

/// <summary>
/// Raw shape of one SoA-extraction document, as produced by the upstream Content
/// Understanding pipeline (e.g. stored in Cosmos DB — extra fields like _rid/_etag on
/// a real document are simply ignored by the deserializer).
/// </summary>
public sealed record SoaExtractionDocument(string? DocumentId, RawSoaTable SoaTable);

/// <summary>The extracted grid: visit columns, procedure rows, which cells are marked, and footnotes.</summary>
public sealed record RawSoaTable(
    string? Caption,
    IReadOnlyList<RawVisit> Visits,
    IReadOnlyList<RawProcedure> Procedures,
    IReadOnlyList<RawScheduleItem>? ScheduleItems, // grid marks; not needed for rule extraction, kept for fidelity
    IReadOnlyList<RawFootnote>? Footnotes);        // used only to recognize footnote markers in procedure names

public sealed record RawVisit(string VisitId, string? Phase, string? Period, string? Week, string StudyDay, string? Hour, string? VisitWindow);
public sealed record RawProcedure(string ProcedureId, string Name);
public sealed record RawScheduleItem(string VisitId, string ProcedureId);
public sealed record RawFootnote(string FootnoteId, string? Text);

/// <summary>Loads a real SoA-extraction document and projects it into the <see cref="SoaVocabulary"/> the extractor uses.</summary>
public static class SoaVocabularyLoader
{
    // PDF-extraction footnote markers left in procedure names, e.g. "Laboratory tests [[6]](162 0 R)".
    private static readonly Regex FootnoteMarker = new(@"\s*\[\[\d+\]\]\(\d+\s+\d+\s+R\)", RegexOptions.Compiled);

    // A trailing parenthetical qualifier, e.g. "Vital signs (incl. Temperature)" -> alias "Vital signs".
    private static readonly Regex ParentheticalQualifier = new(@"^(?<core>.+?)\s*\([^)]*\)\s*$", RegexOptions.Compiled);

    // A leading (optionally negative) integer in a StudyDay label, e.g. "1" from "1 (Predose)",
    // "323" from "323\nEOS", "-28" from "-28".
    private static readonly Regex LeadingDayNumber = new(@"^\s*(-?\d+)", RegexOptions.Compiled);

    public static async Task<SoaVocabulary> FromFileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var doc = await JsonSerializer.DeserializeAsync<SoaExtractionDocument>(stream, Json.File, ct)
            ?? throw new InvalidDataException($"No SoA extraction document in {path}");
        return FromDocument(doc);
    }

    public static SoaVocabulary FromDocument(SoaExtractionDocument doc)
    {
        var visits = doc.SoaTable.Visits.Select(ToVisit).ToList();

        // Some documents (e.g. footnote superscripts appended directly to a procedure
        // name, like "Vital signsg,h") only expose which suffixes are footnote markers
        // via the document's own footnote-id list; others (e.g. "[[6]](162 0 R)") have a
        // self-contained pattern. Both are cleaned below.
        var footnoteIds = (doc.SoaTable.Footnotes ?? [])
            .Select(f => f.FootnoteId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
        var procedures = doc.SoaTable.Procedures.Select(p => ToProcedure(p, footnoteIds)).ToList();

        return new SoaVocabulary(visits, procedures);
    }

    private static SoaVisit ToVisit(RawVisit v)
    {
        var dayLabel = !string.IsNullOrWhiteSpace(v.Period) ? v.Period.Trim() : ExtractDayLabel(v.StudyDay);
        return new SoaVisit(v.VisitId, dayLabel, v.Period, v.StudyDay,
            string.IsNullOrWhiteSpace(v.VisitWindow) ? null : v.VisitWindow);
    }

    /// <summary>Falls back to the leading day number when Period is absent, e.g. "1" from "1 (Predose)".</summary>
    private static string ExtractDayLabel(string studyDay)
    {
        var match = LeadingDayNumber.Match(studyDay);
        if (match.Success)
            return match.Groups[1].Value;
        return studyDay.Split('\n')[0].Trim(); // last resort: first line, e.g. if a future document has no leading number
    }

    private static SoaProcedure ToProcedure(RawProcedure raw, IReadOnlyList<string> footnoteIds)
    {
        var name = FootnoteMarker.Replace(raw.Name, "").Trim();
        name = StripFootnoteSuperscripts(name, footnoteIds);

        var aliases = new List<string>();
        var match = ParentheticalQualifier.Match(name);
        if (match.Success)
            aliases.Add(match.Groups["core"].Value.Trim());
        return new SoaProcedure(raw.ProcedureId, name, aliases);
    }

    /// <summary>
    /// Strips footnote superscripts appended directly to a name with no separator (e.g.
    /// "Vital signsg,h", "...(HIV)P"). Footnote ids are often single letters, which collide
    /// with ordinary word endings (e.g. "Administrative" ends in "e", a valid id, and "BMI"
    /// ends in "I") — so this only strips patterns unambiguous enough to not need a
    /// dictionary: a comma-separated run of 2+ known ids, or a single id immediately after
    /// a closing paren. A single bare trailing letter with no such marker (e.g. "ICFa",
    /// "testingc") is left alone rather than guessed at; see the loader's remarks.
    /// </summary>
    private static string StripFootnoteSuperscripts(string name, IReadOnlyList<string> footnoteIds)
    {
        if (footnoteIds.Count == 0)
            return name;

        var idPattern = string.Join("|", footnoteIds.OrderByDescending(id => id.Length).Select(Regex.Escape));

        // Case-sensitive: real footnote markers are appended in the source's own case, and
        // matching case-sensitively is what keeps this from eating a trailing capital that
        // happens to share a letter with an id (e.g. the "G" in "ECG" vs. id "g").
        var commaList = new Regex($@"(?:{idPattern})(?:,(?:{idPattern}))+$");
        var stripped = commaList.Replace(name, "").TrimEnd();

        // A single id right after ')' is also unambiguous — prose doesn't end a parenthetical
        // and then continue with one bare letter — so this one is safe case-insensitively too
        // (the source sometimes capitalizes it, e.g. "...(HIV)P" for footnote "p").
        var afterParen = new Regex($@"(?<=\))\s*(?:{idPattern})$", RegexOptions.IgnoreCase);
        stripped = afterParen.Replace(stripped, "").TrimEnd();

        return stripped;
    }
}
