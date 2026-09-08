using Microsoft.Build.Locator;

namespace Graphify.CSharp.Roslyn;

internal static class MsBuildEnvironment
{
    private static readonly object RegistrationGate = new();

    public static void EnsureRegistered()
    {
        lock (RegistrationGate)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }
}
