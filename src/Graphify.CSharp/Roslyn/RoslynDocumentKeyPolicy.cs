using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

internal static class RoslynDocumentKeyPolicy
{
    public static IReadOnlyList<RoslynDocumentWork> Create(IEnumerable<Document> documents) =>
        documents
            .Select(document => new RoslynDocumentWork(document, BaseKey(document)))
            .OrderBy(document => document.Key, StringComparer.Ordinal)
            .GroupBy(document => document.Key, StringComparer.Ordinal)
            .SelectMany(group => group.Select((document, duplicateIndex) => document with
            {
                // Roslyn can expose one physical generated file more than
                // once when an explicit broad include overlaps SDK-generated
                // inputs. Keep the path as the stable base key and add an
                // occurrence suffix only for the work-queue identity.
                Key = duplicateIndex == 0
                    ? document.Key
                    : $"{document.Key}\u001f{duplicateIndex:D8}",
            }))
            .ToArray();

    private static string BaseKey(Document document) =>
        string.IsNullOrWhiteSpace(document.FilePath)
            ? document.Name
            : Path.GetFullPath(document.FilePath).Replace('\\', '/');
}

internal sealed record RoslynDocumentWork(Document Document, string Key);
