using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoGeoEditor.Core;

public enum AppTheme { Light, Dark }

/// <summary>Пользовательские настройки приложения.</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Light;

    public WriteMode WriteMode { get; set; } = WriteMode.SaveCopies;

    public bool ShowDirectionArrows { get; set; } = true;

    public bool IncludeSubfolders { get; set; } = false;

    public int ThumbnailSize { get; set; } = 56;

    /// <summary>Шаблон URL тайлов карты (по умолчанию OpenStreetMap).</summary>
    public string TileUrl { get; set; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    public string? LastFolder { get; set; }
}

/// <summary>Хранение настроек в JSON: %AppData%\PhotoGeoEditor\settings.json.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SettingsService(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PhotoGeoEditor", "settings.json");
    }

    public string FilePath { get; }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch
        {
            // битый файл настроек не должен мешать запуску — берём значения по умолчанию
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
    }
}
