namespace SoaRuleExtractor.Models;

/// <summary>One pre-chunked protocol section.</summary>
public sealed record Section(
    string SectionId,        // e.g. "8.4.2"
    string HeadingPath,      // e.g. "8 Study Assessments > 8.4 PK > 8.4.2 PK Sampling"
    string Markdown);        // section body, markdown from Content Understanding
