namespace MediaModule.Infrastructure.PathResolution;

internal static class ModulePathResolver
{
    public static string Resolve(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return AppContext.BaseDirectory;
        }

        configuredPath = Environment.ExpandEnvironmentVariables(configuredPath);

        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var solutionRoot = TryFindSolutionRoot();
        return solutionRoot is null
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath))
            : Path.GetFullPath(Path.Combine(solutionRoot, configuredPath));
    }

    private static string? TryFindSolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MediaModule.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
