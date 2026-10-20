using System.Runtime.CompilerServices;

namespace Toolbox.Core.Tests;

internal static class TestEnvironment
{
    /// <summary>Keeps logs, settings and history written during tests out of the real profile.</summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ftb-tests-data");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(AppPaths.DataEnvironmentVariable, dir);
    }
}
