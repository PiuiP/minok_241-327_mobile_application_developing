using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoGeoEditor.Core;

/// <summary>Положение карты (центр и масштаб).</summary>
public sealed class MapView
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int Zoom { get; set; }
}

/// <summary>Состояние одной фотографии в сохранённом проекте.</summary>
public sealed class PhotoState
{
    public string Path { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? Altitude { get; set; }
    public double? Direction { get; set; }
    public DateTime? GpsTimeUtc { get; set; }
    public string? ProcessingMethod { get; set; }
    public string? MapDatum { get; set; }
}

/// <summary>Состояние работающего приложения: что открыто, какие правки сделаны, где стоит карта.</summary>
public sealed class ProjectState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public MapView? Map { get; set; }
    public string? SelectedPath { get; set; }
    public List<PhotoState> Photos { get; set; } = new();
}

/// <summary>Сохранение и загрузка состояния в JSON-файл (*.pgeo.json).</summary>
public sealed class ProjectStateService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ProjectState Capture(PhotoLibrary library, MapView? view, string? selectedPath)
    {
        var state = new ProjectState { Map = view, SelectedPath = selectedPath };
        foreach (var p in library.Photos)
        {
            state.Photos.Add(new PhotoState
            {
                Path = p.FilePath,
                Latitude = p.Latitude,
                Longitude = p.Longitude,
                Altitude = p.Altitude,
                Direction = p.Direction,
                GpsTimeUtc = p.GpsTimeUtc,
                ProcessingMethod = p.ProcessingMethod,
                MapDatum = p.MapDatum
            });
        }
        return state;
    }

    public void Save(string path, ProjectState state)
        => File.WriteAllText(path, JsonSerializer.Serialize(state, Options));

    public ProjectState Load(string path)
    {
        var state = JsonSerializer.Deserialize<ProjectState>(File.ReadAllText(path), Options)
                    ?? throw new InvalidDataException("Project file is empty.");
        if (state.Version > ProjectState.CurrentVersion)
            throw new InvalidDataException($"Project file version {state.Version} is newer than supported ({ProjectState.CurrentVersion}).");
        return state;
    }

    /// <summary>
    /// Заменяет содержимое библиотеки фотографиями из состояния. Исходные значения берутся из файлов,
    /// а правки из проекта накладываются сверху (поэтому фото будут помечены как изменённые).
    /// Возвращает список файлов, которые не удалось открыть.
    /// </summary>
    public IReadOnlyList<string> Restore(PhotoLibrary library, ProjectState state)
    {
        var problems = new List<string>();
        library.Clear();

        foreach (var ps in state.Photos)
        {
            if (!File.Exists(ps.Path))
            {
                problems.Add($"{ps.Path}: file not found");
                continue;
            }

            try
            {
                var item = library.ReadItem(ps.Path);
                item.ApplyGeo(new GeoData
                {
                    Latitude = ps.Latitude,
                    Longitude = ps.Longitude,
                    Altitude = ps.Altitude,
                    Direction = ps.Direction,
                    GpsTimeUtc = ps.GpsTimeUtc,
                    ProcessingMethod = ps.ProcessingMethod,
                    MapDatum = ps.MapDatum,
                    DateTaken = item.Original.DateTaken
                });
                library.Add(item);
            }
            catch (Exception ex)
            {
                problems.Add($"{ps.Path}: {ex.Message}");
            }
        }

        return problems;
    }
}
