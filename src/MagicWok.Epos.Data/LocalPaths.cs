namespace MagicWok.Epos.Data;

/// <summary>SQLite path under %APPDATA%\MagicWok.Epos\</summary>
public static class LocalPaths
{
    public static string AppDataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MagicWok.Epos");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DatabasePath => Path.Combine(AppDataDirectory, "data.sqlite");
}
