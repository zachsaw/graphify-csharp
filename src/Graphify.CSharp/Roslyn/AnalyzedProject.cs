using Graphify.CSharp.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Graphify.CSharp.Roslyn;

public sealed class AnalyzedProject
{
    public AnalyzedProject(Project project, ProjectIdentity identity, Compilation compilation)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
    }

    public Project Project { get; }

    public ProjectIdentity Identity { get; }

    public Compilation Compilation { get; }
}
