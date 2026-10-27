using Microsoft.CodeAnalysis;

namespace Graphify.CSharp.Roslyn;

internal static class RoslynSyntaxTreeKeyPolicy
{
    public static IReadOnlyList<RoslynSyntaxTreeWork> Create(IEnumerable<SyntaxTree> trees) =>
        trees
            .Select(tree => new RoslynSyntaxTreeWork(tree, BaseKey(tree)))
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

    private static string BaseKey(SyntaxTree tree) =>
        string.IsNullOrWhiteSpace(tree.FilePath)
            ? "<unknown>"
            : Path.GetFullPath(tree.FilePath).Replace('\\', '/');
}

internal sealed record RoslynSyntaxTreeWork(SyntaxTree Tree, string Key);
