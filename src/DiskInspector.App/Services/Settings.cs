using System.Text.Json;
using DiskInspector.Core.Analysis;

namespace DiskInspector.App.Services;

/// <summary>User preferences, saved to %APPDATA%\DiskInspector\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Where "Snapshot" saves CSV files.</summary>
    public string SnapshotFolder { get; set; } = Snapshots.DefaultFolder;

    /// <summary>Read author / last saved by / title from document files (read-only, last-access date kept).</summary>
    public bool ReadAuthors { get; set; } = true;

    /// <summary>Read authors automatically when a work-files table has at most this many readable files.</summary>
    public int AuthorAutoLimit { get; set; } = 2000;

    /// <summary>Work files: skip Windows, Program Files, AppData and package/cache folders.</summary>
    public bool OnlyUserFolders { get; set; } = true;

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskInspector", "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return Sanitize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings());
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Settings file could not be read; using defaults", e);
        }
        return new AppSettings();
    }

    static AppSettings Sanitize(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.SnapshotFolder))
            s.SnapshotFolder = Snapshots.DefaultFolder;
        s.AuthorAutoLimit = Math.Clamp(s.AuthorAutoLimit, 0, 1_000_000);
        return s;
    }

    /// <summary>Saves the settings; returns an error message, or null on success.</summary>
    public string? Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Sanitize(this), Json));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Settings could not be saved", e);
            return e.Message;
        }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
