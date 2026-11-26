using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoGeoEditor.Core;

/// <summary>
/// Создаёт тестовые «фотографии» (цветные картинки) с GPS в EXIF вокруг центра Москвы.
/// Последняя картинка намеренно без координат — на ней можно проверить «поставить фото на карту».
/// </summary>
public static class SampleGenerator
{
    public static IReadOnlyList<string> Generate(string folder, int count, IGeoMetadataService metadata, int seed = 42)
    {
        Directory.CreateDirectory(folder);
        var random = new Random(seed);
        var paths = new List<string>();

        for (var i = 1; i <= count; i++)
        {
            var path = Path.Combine(folder, $"photo_{i:00}.jpg");

            using (var image = new Image<Rgba32>(320, 240, FromHue(360.0 * (i - 1) / count)))
                image.SaveAsJpeg(path);

            var withoutGps = count >= 3 && i == count;
            var geo = new GeoData
            {
                DateTaken = DateTime.Today.AddHours(10).AddMinutes(i * 7),
                Latitude = withoutGps ? null : Math.Round(55.7539 + (random.NextDouble() - 0.5) * 0.06, 6),
                Longitude = withoutGps ? null : Math.Round(37.6208 + (random.NextDouble() - 0.5) * 0.10, 6),
                Altitude = withoutGps ? null : Math.Round(130 + random.NextDouble() * 40, 1),
                Direction = withoutGps ? null : Math.Round(random.NextDouble() * 360, 1),
                GpsTimeUtc = withoutGps ? null : DateTime.SpecifyKind(DateTime.Today.AddHours(7).AddMinutes(i * 7), DateTimeKind.Utc),
                ProcessingMethod = withoutGps ? null : "GPS",
                MapDatum = withoutGps ? null : "WGS-84"
            };

            metadata.Write(path, path, geo);
            paths.Add(path);
        }

        return paths;
    }

    private static Rgba32 FromHue(double hue)
    {
        const double c = 0.7, m = 0.25;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        (double r, double g, double b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return new Rgba32((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
