using SoaRuleExtractor.Models;

namespace SoaRuleExtractor.Sources;

/// <summary>Supplies protocol sections to the extractor.</summary>
public interface ISectionSource
{
    IAsyncEnumerable<Section> GetSectionsAsync(CancellationToken ct = default);
}
