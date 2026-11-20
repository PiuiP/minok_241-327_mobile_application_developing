using System.Globalization;
using PhotoGeoEditor.Core;

namespace PhotoGeoEditor;

/// <summary>
/// Консольный клиент. Весь вывод — только на английском (консоль не поддерживает русский).
/// Вся логика живёт в библиотеке PhotoGeoEditor.Core, здесь только разбор аргументов и печать.
/// </summary>
internal static class Program
{
    private static readonly IGeoMetadataService Metadata = new ExifGeoMetadataService();

    private const string Usage = """
        PhotoGeoEditor - view and edit the GPS position of photos stored in EXIF

        Usage:
          PhotoGeoEditor <command> [arguments] [options]

        Commands:
          list <folder> [--recursive]
              Print a table of photos with latitude, longitude, altitude, direction.
          show <file>
              Print all geo fields of one photo.
          set <file> [--lat <deg>] [--lon <deg>] [--alt <m>] [--dir <deg>]
                     [--method <text>] [--time "yyyy-MM-dd HH:mm:ss"]
                     [--out <file> | --overwrite]
              Change the position. By default a copy is written to <folder>/geo_edited/.
          clear <file> [--out <file> | --overwrite]
              Remove GPS data from the photo.
          export <folder> [--format geojson|csv] [--out <file>] [--recursive]
              Export photos with GPS to GeoJSON or CSV.
          generate <folder> [--count <n>]
              Create sample photos with GPS in EXIF (the last one has no GPS).
          state <file.pgeo.json>
              Print a summary of a saved application state.
          help
              Show this text.

        Examples:
          PhotoGeoEditor generate demo_photos --count 8
          PhotoGeoEditor list demo_photos
          PhotoGeoEditor set demo_photos/photo_01.jpg --lat 55.7512 --lon 37.6184 --dir 90
          PhotoGeoEditor export demo_photos --format geojson --out demo.geojson
        """;

    private static int Main(string[] args)
    {
        // Сообщения системных исключений тоже должны быть на английском.
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        if (args.Length == 0)
        {
            Console.WriteLine(Usage);
            return 2;
        }

        if (args[0] is "-h" or "--help" or "help" or "/?")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        try
        {
            var command = args[0].ToLowerInvariant();
            var parsed = ParsedArgs.Parse(args.Skip(1));

            if (parsed.Has("help"))
            {
                Console.WriteLine(Usage);
                return 0;
            }

            switch (command)
            {
                case "list": return List(parsed);
                case "show": return Show(parsed);
                case "set": return Set(parsed);
                case "clear": return Clear(parsed);
                case "export": return Export(parsed);
                case "generate": return Generate(parsed);
                case "state": return StateInfo(parsed);
                default:
                    Console.Error.WriteLine($"Unknown command: {args[0]}");
                    Console.Error.WriteLine("Run 'PhotoGeoEditor help' for usage.");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    // ------------------------------------------------------------------ commands

    private static int List(ParsedArgs a)
    {
        var folder = a.RequirePositional(0, "folder");
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Folder not found: {folder}");

        var files = PhotoLibrary.EnumerateImages(folder, a.Has("recursive")).ToList();
        Console.WriteLine($"{"File",-30} {"Latitude",11} {"Longitude",11} {"Alt(m)",8} {"Dir(deg)",9}");

        var withGps = 0;
        foreach (var file in files)
        {
            var name = Truncate(Path.GetRelativePath(folder, file), 30);
            try
            {
                var g = Metadata.Read(file);
                if (g.HasPosition) withGps++;
                Console.WriteLine($"{name,-30} {F(g.Latitude, "F6"),11} {F(g.Longitude, "F6"),11} {F(g.Altitude, "F1"),8} {F(g.Direction, "F1"),9}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{name,-30} ERROR: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {files.Count} file(s), with GPS: {withGps}");
        return 0;
    }

    private static int Show(ParsedArgs a)
    {
        var file = a.RequirePositional(0, "file");
        PrintGeo(file, Metadata.Read(file));
        return 0;
    }

    private static int Set(ParsedArgs a)
    {
        var file = a.RequirePositional(0, "file");
        var current = Metadata.Read(file);

        var lat = a.Double("lat") ?? current.Latitude;
        var lon = a.Double("lon") ?? current.Longitude;
        if (lat is null || lon is null)
            throw new ArgumentException("The photo has no GPS position yet: both --lat and --lon are required.");
        if (!GeoMath.IsValidLatitude(lat.Value)) throw new ArgumentException("Latitude must be between -90 and 90.");
        if (!GeoMath.IsValidLongitude(lon.Value)) throw new ArgumentException("Longitude must be between -180 and 180.");

        var direction = a.Double("dir");
        var data = current with
        {
            Latitude = lat,
            Longitude = lon,
            Altitude = a.Double("alt") ?? current.Altitude,
            Direction = direction.HasValue ? GeoMath.NormalizeDirection(direction.Value) : current.Direction,
            ProcessingMethod = a.Get("method") ?? current.ProcessingMethod,
            GpsTimeUtc = a.Time("time") ?? current.GpsTimeUtc
        };

        var target = ResolveTarget(a, file);
        Metadata.Write(file, target, data);

        Console.WriteLine($"Written: {target}");
        PrintGeo(target, Metadata.Read(target));
        return 0;
    }

    private static int Clear(ParsedArgs a)
    {
        var file = a.RequirePositional(0, "file");
        var current = Metadata.Read(file);

        var target = ResolveTarget(a, file);
        Metadata.Write(file, target, new GeoData { DateTaken = current.DateTaken });

        Console.WriteLine($"GPS data removed. Written: {target}");
        return 0;
    }

    private static int Export(ParsedArgs a)
    {
        var folder = a.RequirePositional(0, "folder");
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Folder not found: {folder}");

        var format = a.Get("format") ?? "geojson";
        var exporter = Exporters.ByName(format)
                       ?? throw new ArgumentException($"Unknown format '{format}'. Use geojson or csv.");
        var output = a.Get("out") ?? "photos" + exporter.Extension;

        var library = new PhotoLibrary(Metadata);
        var items = new List<PhotoItem>();
        foreach (var file in PhotoLibrary.EnumerateImages(folder, a.Has("recursive")))
        {
            try { items.Add(library.ReadItem(file)); }
            catch (Exception ex) { Console.WriteLine($"Skipped {Path.GetFileName(file)}: {ex.Message}"); }
        }

        exporter.Export(items, output);
        Console.WriteLine($"Exported {items.Count(i => i.HasPosition)} photo(s) with GPS to {Path.GetFullPath(output)}");
        return 0;
    }

    private static int Generate(ParsedArgs a)
    {
        var folder = a.RequirePositional(0, "folder");
        var count = a.Int("count") ?? 12;
        if (count < 1 || count > 500) throw new ArgumentException("--count must be between 1 and 500.");

        var paths = SampleGenerator.Generate(folder, count, Metadata);
        foreach (var p in paths) Console.WriteLine($"Created: {p}");
        Console.WriteLine($"Done: {paths.Count} file(s).");
        return 0;
    }

    private static int StateInfo(ParsedArgs a)
    {
        var path = a.RequirePositional(0, "file.pgeo.json");
        var state = new ProjectStateService().Load(path);

        Console.WriteLine($"State file: {Path.GetFullPath(path)}");
        Console.WriteLine($"Version: {state.Version}");
        if (state.Map is { } m)
            Console.WriteLine($"Map view: center {m.Latitude.ToString("F5", CultureInfo.InvariantCulture)}, {m.Longitude.ToString("F5", CultureInfo.InvariantCulture)}, zoom {m.Zoom}");
        Console.WriteLine($"Selected: {state.SelectedPath ?? "-"}");
        Console.WriteLine($"Photos: {state.Photos.Count}");
        foreach (var p in state.Photos)
            Console.WriteLine($"  {Path.GetFileName(p.Path),-28} {F(p.Latitude, "F6"),11} {F(p.Longitude, "F6"),11}");
        return 0;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Куда писать результат: --out, --overwrite или (по умолчанию) копия в подпапке geo_edited.</summary>
    private static string ResolveTarget(ParsedArgs a, string source)
    {
        var explicitOut = a.Get("out");
        if (explicitOut is not null) return explicitOut;
        if (a.Has("overwrite")) return source;

        var dir = Path.GetDirectoryName(Path.GetFullPath(source))!;
        return Path.Combine(dir, PhotoLibrary.OutputFolderName, Path.GetFileName(source));
    }

    private static void PrintGeo(string file, GeoData g)
    {
        Console.WriteLine($"File:          {file}");
        Console.WriteLine($"Date taken:    {(g.DateTaken?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-")}");

        if (!g.HasPosition)
        {
            Console.WriteLine("GPS position:  none");
            return;
        }

        Console.WriteLine($"Latitude:      {F(g.Latitude, "F6")}  ({GeoMath.FormatDms(g.Latitude!.Value, true)})");
        Console.WriteLine($"Longitude:     {F(g.Longitude, "F6")}  ({GeoMath.FormatDms(g.Longitude!.Value, false)})");
        Console.WriteLine($"Altitude:      {(g.Altitude.HasValue ? F(g.Altitude, "F1") + " m" : "-")}");
        Console.WriteLine($"Direction:     {(g.Direction.HasValue ? F(g.Direction, "F1") + " deg" : "-")}");
        Console.WriteLine($"GPS time UTC:  {(g.GpsTimeUtc?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-")}");
        Console.WriteLine($"Method:        {g.ProcessingMethod ?? "-"}");
        Console.WriteLine($"Map datum:     {g.MapDatum ?? "-"}");
    }

    private static string F(double? value, string format)
        => value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "-";

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : "..." + s[^(max - 3)..];
}
