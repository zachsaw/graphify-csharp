using Graphify.CSharp.Domain;
using Graphify.CSharp.Incremental;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text;

namespace Graphify.CSharp.Roslyn;

internal sealed class IncrementalProjectFingerprintBuilder
{
    public IReadOnlyDictionary<string, ProjectFingerprint> BuildAll(LoadedSolution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        var fingerprints = new Dictionary<string, ProjectFingerprint>(StringComparer.Ordinal);
        foreach (var project in solution.Projects.OrderBy(project => project.Identity.Key, StringComparer.Ordinal))
        {
            fingerprints.Add(project.Identity.Key, Build(solution, project));
        }

        return fingerprints;
    }

    public ProjectFingerprint Build(LoadedSolution solution, AnalyzedProject project, bool includeContentHashes = false)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(project);

        var sourceFiles = project.Project.Documents
            .Select(document => document.FilePath)
            .OfType<string>()
            .Select(Path.GetFullPath)
            .Distinct(GetPathComparer())
            .OrderBy(path => path, GetPathComparer())
            .Select(path => SourceFingerprint.FromFile(path, solution.RepositoryRoot, includeContentHashes));
        var inputDiscovery = solution.GetInputDiscovery(project);
        var dependencyFiles = inputDiscovery.Paths
            .Select(Path.GetFullPath)
            .Distinct(GetPathComparer())
            .OrderBy(path => path, GetPathComparer())
            .Select(path => SourceFingerprint.FromFile(path, solution.RepositoryRoot, includeContentHashes));
        var projectFile = FindProjectFile(solution, project.Identity);
        var references = project.Project.ProjectReferences
            .Select(reference => FindReferenceKey(solution, reference))
            .OfType<string>()
            .OrderBy(key => key, StringComparer.Ordinal);

        return new ProjectFingerprint(
            project.Identity,
            projectFile is null
                ? null
                : SourceFingerprint.FromFile(projectFile, solution.RepositoryRoot, includeContentHashes),
            sourceFiles,
            references,
            dependencyFiles,
            inputDiscovery.IsComplete,
            GetCompilationOptionsKey(project.Project));
    }

    private static string? FindProjectFile(LoadedSolution solution, ProjectIdentity identity)
    {
        if (!identity.RelativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var projectPath = Path.GetFullPath(
            Path.Combine(
                solution.RepositoryRoot,
                identity.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(projectPath) ? projectPath : null;
    }

    private static string? FindReferenceKey(LoadedSolution solution, ProjectReference reference)
    {
        var referencedProject = solution.Projects.FirstOrDefault(project => project.Project.Id == reference.ProjectId);
        if (referencedProject is null)
        {
            return null;
        }

        var aliases = string.Join(",", reference.Aliases.OrderBy(alias => alias, StringComparer.Ordinal));
        return string.Join(
            '\u001F',
            referencedProject.Identity.Key,
            $"aliases={CanonicalText.Escape(aliases)}");
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string GetCompilationOptionsKey(Project project)
    {
        return CreateCompilationOptionsKey(project.ParseOptions, project.CompilationOptions);
    }

    internal static string CreateCompilationOptionsKey(
        ParseOptions? parseOptions,
        CompilationOptions? compilationOptions)
    {
        var key = new StringBuilder();
        if (parseOptions is null)
        {
            Append(key, "parse", null);
        }
        else
        {
            Append(key, "parse.type", parseOptions.GetType().FullName);
            Append(key, "parse.language", parseOptions.Language);
            Append(key, "parse.kind", parseOptions.Kind.ToString());
            Append(key, "parse.specified-kind", parseOptions.SpecifiedKind.ToString());
            Append(key, "parse.documentation", parseOptions.DocumentationMode.ToString());
            AppendStrings(key, "parse.preprocessor-symbols", parseOptions.PreprocessorSymbolNames);
            AppendKeyValues(key, "parse.features", parseOptions.Features);
            if (parseOptions is CSharpParseOptions csharpParseOptions)
            {
                Append(key, "parse.language-version", csharpParseOptions.LanguageVersion.ToString());
                Append(key, "parse.specified-language-version", csharpParseOptions.SpecifiedLanguageVersion.ToString());
            }
        }

        if (compilationOptions is null)
        {
            Append(key, "compilation", null);
        }
        else
        {
            Append(key, "compilation.type", compilationOptions.GetType().FullName);
            Append(key, "compilation.output-kind", compilationOptions.OutputKind.ToString());
            Append(key, "compilation.module-name", compilationOptions.ModuleName);
            Append(key, "compilation.script-class-name", compilationOptions.ScriptClassName);
            Append(key, "compilation.main-type-name", compilationOptions.MainTypeName);
            Append(key, "compilation.crypto-public-key", compilationOptions.CryptoPublicKey.IsDefaultOrEmpty
                ? null
                : Convert.ToHexString(compilationOptions.CryptoPublicKey.ToArray()));
            Append(key, "compilation.crypto-key-file", compilationOptions.CryptoKeyFile);
            Append(key, "compilation.crypto-key-container", compilationOptions.CryptoKeyContainer);
            Append(key, "compilation.delay-sign", compilationOptions.DelaySign?.ToString());
            Append(key, "compilation.public-sign", compilationOptions.PublicSign.ToString());
            Append(key, "compilation.check-overflow", compilationOptions.CheckOverflow.ToString());
            Append(key, "compilation.platform", compilationOptions.Platform.ToString());
            Append(key, "compilation.optimization", compilationOptions.OptimizationLevel.ToString());
            Append(key, "compilation.general-diagnostic", compilationOptions.GeneralDiagnosticOption.ToString());
            Append(key, "compilation.warning-level", compilationOptions.WarningLevel.ToString());
            Append(key, "compilation.concurrent-build", compilationOptions.ConcurrentBuild.ToString());
            Append(key, "compilation.deterministic", compilationOptions.Deterministic.ToString());
            Append(key, "compilation.metadata-import", compilationOptions.MetadataImportOptions.ToString());
            Append(key, "compilation.report-suppressed-diagnostics", compilationOptions.ReportSuppressedDiagnostics.ToString());
            Append(key, "compilation.nullable-context", compilationOptions.NullableContextOptions.ToString());
            AppendKeyValues(key, "compilation.specific-diagnostics", compilationOptions.SpecificDiagnosticOptions
                .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value.ToString())));
            AppendStrings(key, "compilation.usings", (compilationOptions as CSharpCompilationOptions)?.Usings);
            if (compilationOptions is CSharpCompilationOptions csharpCompilationOptions)
            {
                Append(key, "compilation.allow-unsafe", csharpCompilationOptions.AllowUnsafe.ToString());
            }

            AppendProviderType(key, "compilation.syntax-tree-options-provider", compilationOptions.SyntaxTreeOptionsProvider);
            AppendProviderType(key, "compilation.metadata-reference-resolver", compilationOptions.MetadataReferenceResolver);
            AppendProviderType(key, "compilation.xml-reference-resolver", compilationOptions.XmlReferenceResolver);
            AppendProviderType(key, "compilation.source-reference-resolver", compilationOptions.SourceReferenceResolver);
            AppendProviderType(key, "compilation.strong-name-provider", compilationOptions.StrongNameProvider);
            AppendProviderType(key, "compilation.assembly-identity-comparer", compilationOptions.AssemblyIdentityComparer);
        }

        return key.ToString();
    }

    private static void Append(StringBuilder key, string name, string? value)
    {
        key.Append(name)
            .Append('=')
            .Append(Encode(value))
            .Append('\u001F');
    }

    private static void AppendStrings(StringBuilder key, string name, IEnumerable<string>? values) =>
        Append(
            key,
            name,
            values is null
                ? null
                : string.Join(
                    ',',
                    values
                        .Where(value => value is not null)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .Select(Encode)));

    private static void AppendKeyValues(
        StringBuilder key,
        string name,
        IEnumerable<KeyValuePair<string, string>>? values) =>
        Append(
            key,
            name,
            values is null
                ? null
                : string.Join(
                    ',',
                    values
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .ThenBy(pair => pair.Value, StringComparer.Ordinal)
                        .Select(pair => $"{Encode(pair.Key)}:{Encode(pair.Value)}")));

    private static void AppendProviderType(StringBuilder key, string name, object? provider) =>
        Append(key, name, provider?.GetType().FullName);

    private static string Encode(string? value) =>
        value is null
            ? "<null>"
            : Convert.ToHexString(Encoding.UTF8.GetBytes(value));
}
