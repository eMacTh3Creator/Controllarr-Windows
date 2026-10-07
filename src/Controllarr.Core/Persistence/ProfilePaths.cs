namespace Controllarr.Core.Persistence;

public static class ProfilePaths
{
    public const string EnvironmentVariable = "CONTROLLARR_PROFILE_DIRECTORY";
    public static bool IsCustom => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable));
    public static string CurrentDirectory
    {
        get
        {
            string? custom = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(custom))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Controllarr");
            if (!Path.IsPathFullyQualified(custom)) throw new ArgumentException($"{EnvironmentVariable} must be an absolute folder path.");
            return Path.GetFullPath(custom);
        }
    }
}
