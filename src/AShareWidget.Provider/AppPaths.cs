namespace AShareWidget;

/// <summary>
/// Visible per-user data dir (bypasses MSIX LocalAppData redirection):
/// %USERPROFILE%\AppData\Local\AShareWidget\
/// </summary>
static class AppPaths
{
    public const string AppFolderName = "AShareWidget";

    public static string UserDataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "Local",
                AppFolderName);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
