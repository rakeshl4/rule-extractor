using System.Runtime.CompilerServices;
using System.Text.Json;
using SoaRuleExtractor.Models;

namespace SoaRuleExtractor.Sources;

/// <summary>Stub source that reads a JSON array of <see cref="Section"/> from disk.</summary>
public sealed class JsonFileSectionSource(string path) : ISectionSource
{
    public async IAsyncEnumerable<Section> GetSectionsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var sections = await JsonSerializer.DeserializeAsync<List<Section>>(stream, Json.File, ct)
            ?? throw new InvalidDataException($"No sections in {path}");
        foreach (var section in sections)
        {
            ct.ThrowIfCancellationRequested();
            yield return section;
        }
    }
}
