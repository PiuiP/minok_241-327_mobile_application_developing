using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhotoGeoEditor.Core;

/// <summary>Экспорт списка фотографий в файл. Реализации: GeoJSON, CSV.</summary>
public interface IPhotoExporter
{
    string Name { get; }
    string Extension { get; }
    void Export(IEnumerable<PhotoItem> photos, string path);
}

public static class Exporters
{
    public static IReadOnlyList<IPhotoExporter> All { get; } = new IPhotoExporter[] { new GeoJsonExporter(), new CsvExporter() };

    public static IPhotoExporter? ByName(string name)
        => All.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>GeoJSON: открывается в geojson.io, QGIS и т.п. Порядок координат: [долгота, широта, высота].</summary>
public sealed class GeoJsonExporter : IPhotoExporter
{
    public string Name => "geojson";
    public string Extension => ".geojson";

    public void Export(IEnumerable<PhotoItem> photos, string path)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        w.WriteStartObject();
        w.WriteString("type", "FeatureCollection");
        w.WriteStartArray("features");

        foreach (var p in photos.Where(p => p.HasPosition))
        {
            w.WriteStartObject();
            w.WriteString("type", "Feature");

            w.WriteStartObject("geometry");
            w.WriteString("type", "Point");
            w.WriteStartArray("coordinates");
            w.WriteNumberValue(p.Longitude!.Value);
            w.WriteNumberValue(p.Latitude!.Value);
            if (p.Altitude is { } alt) w.WriteNumberValue(alt);
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject("properties");
            w.WriteString("file", p.FileName);
            if (p.Direction is { } dir) w.WriteNumber("direction", dir);
            if (p.GpsTimeUtc is { } t) w.WriteString("gpsTimeUtc", t);
            if (p.ProcessingMethod is { } m) w.WriteString("processingMethod", m);
            w.WriteEndObject();

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }
}

public sealed class CsvExporter : IPhotoExporter
{
    public string Name => "csv";
    public string Extension => ".csv";

    public void Export(IEnumerable<PhotoItem> photos, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("file,latitude,longitude,altitude,direction,gps_time_utc,method,datum,modified");

        foreach (var p in photos)
        {
            sb.AppendLine(string.Join(',',
                Escape(p.FileName),
                Num(p.Latitude), Num(p.Longitude), Num(p.Altitude), Num(p.Direction),
                p.GpsTimeUtc?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                Escape(p.ProcessingMethod ?? ""), Escape(p.MapDatum ?? ""),
                p.IsModified ? "yes" : "no"));
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Num(double? v) => v?.ToString("R", CultureInfo.InvariantCulture) ?? "";

    private static string Escape(string s)
        => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
