namespace Graphify.CSharp.Roslyn;

public interface IProjectLoader
{
    Task<LoadedSolution> LoadAsync(ProjectLoadRequest request, CancellationToken cancellationToken = default);
}
