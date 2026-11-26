using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace PhotoGeoEditor.Core;

/// <summary>Реализация IGeoMetadataService на SixLabors.ImageSharp (JPEG, PNG, WebP, TIFF).</summary>
public sealed class ExifGeoMetadataService : IGeoMetadataService
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // Все теги, относящиеся к GPS, которыми мы управляем. Перед записью сначала удаляем их, потом пишем нужные.
    private static readonly ExifTag[] GpsTags =
    {
        ExifTag.GPSVersionID,
        ExifTag.GPSLatitude, ExifTag.GPSLatitudeRef,
        ExifTag.GPSLongitude, ExifTag.GPSLongitudeRef,
        ExifTag.GPSAltitude, ExifTag.GPSAltitudeRef,
        ExifTag.GPSImgDirection, ExifTag.GPSImgDirectionRef,
        ExifTag.GPSTimestamp, ExifTag.GPSDateStamp,
        ExifTag.GPSProcessingMethod, ExifTag.GPSMapDatum
    };

    // ---------------------------------------------------------------- чтение

    public GeoData Read(string path)
    {
        // Identify читает только заголовок и метаданные, пиксели не декодирует — это быстро.
        var info = Image.Identify(path);
        var exif = info.Metadata.ExifProfile;
        if (exif is null) return new GeoData();

        double? altitude = ReadRational(exif, ExifTag.GPSAltitude);
        if (altitude.HasValue && exif.TryGetValue(ExifTag.GPSAltitudeRef, out var altRef) && altRef.Value == 1)
            altitude = -altitude.Value; // 1 = ниже уровня моря

        string? method = null;
        if (exif.TryGetValue(ExifTag.GPSProcessingMethod, out var pm))
            method = NullIfEmpty(pm.Value.Text);

        string? datum = null;
        if (exif.TryGetValue(ExifTag.GPSMapDatum, out var md))
            datum = NullIfEmpty(md.Value);

        DateTime? taken = null;
        if (exif.TryGetValue(ExifTag.DateTimeOriginal, out var dto) &&
            DateTime.TryParseExact(dto.Value, "yyyy:MM:dd HH:mm:ss", Invariant, DateTimeStyles.None, out var parsed))
            taken = parsed;

        return new GeoData
        {
            Latitude = ReadCoordinate(exif, ExifTag.GPSLatitude, ExifTag.GPSLatitudeRef, "S"),
            Longitude = ReadCoordinate(exif, ExifTag.GPSLongitude, ExifTag.GPSLongitudeRef, "W"),
            Altitude = altitude,
            Direction = ReadRational(exif, ExifTag.GPSImgDirection),
            GpsTimeUtc = ReadGpsTime(exif),
            ProcessingMethod = method,
            MapDatum = datum,
            DateTaken = taken
        };
    }

    private static double? ReadCoordinate(ExifProfile exif, ExifTag<Rational[]> valueTag, ExifTag<string> refTag, string negativeRef)
    {
        if (!exif.TryGetValue(valueTag, out var value) || value.Value is not { Length: 3 } r) return null;

        var result = r[0].ToDouble() + r[1].ToDouble() / 60.0 + r[2].ToDouble() / 3600.0;
        if (!double.IsFinite(result)) return null;

        if (exif.TryGetValue(refTag, out var hemisphere) &&
            string.Equals(hemisphere.Value?.Trim(), negativeRef, StringComparison.OrdinalIgnoreCase))
            result = -result;

        return result;
    }

    private static double? ReadRational(ExifProfile exif, ExifTag<Rational> tag)
    {
        if (!exif.TryGetValue(tag, out var value)) return null;
        var d = value.Value.ToDouble();
        return double.IsFinite(d) ? d : null;
    }

    private static DateTime? ReadGpsTime(ExifProfile exif)
    {
        if (!exif.TryGetValue(ExifTag.GPSDateStamp, out var dateStamp) ||
            !exif.TryGetValue(ExifTag.GPSTimestamp, out var timeStamp)) return null;

        if (timeStamp.Value is not { Length: 3 } t) return null;
        if (!DateTime.TryParseExact(dateStamp.Value, "yyyy:MM:dd", Invariant, DateTimeStyles.None, out var date)) return null;

        var time = date.AddHours(t[0].ToDouble()).AddMinutes(t[1].ToDouble()).AddSeconds(t[2].ToDouble());
        return DateTime.SpecifyKind(time, DateTimeKind.Utc);
    }

    // ---------------------------------------------------------------- запись

    public void Write(string sourcePath, string targetPath, GeoData data)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullTarget)!;
        Directory.CreateDirectory(directory);

        // Пишем во временный файл рядом (с тем же расширением — по нему ImageSharp выбирает кодер),
        // и только потом заменяем целевой. Если что-то упадёт, оригинал останется целым.
        var temp = Path.Combine(directory,
            Path.GetFileNameWithoutExtension(fullTarget) + ".pgeo-tmp" + Path.GetExtension(fullTarget));

        try
        {
            using (var image = Image.Load(sourcePath))
            {
                image.Metadata.ExifProfile ??= new ExifProfile();
                var exif = image.Metadata.ExifProfile!;

                foreach (var tag in GpsTags) exif.RemoveValue(tag);

                if (data.HasPosition)
                {
                    exif.SetValue(ExifTag.GPSVersionID, new byte[] { 2, 3, 0, 0 });
                    WriteCoordinate(exif, ExifTag.GPSLatitude, ExifTag.GPSLatitudeRef, data.Latitude!.Value, "N", "S");
                    WriteCoordinate(exif, ExifTag.GPSLongitude, ExifTag.GPSLongitudeRef, data.Longitude!.Value, "E", "W");
                    exif.SetValue(ExifTag.GPSMapDatum, string.IsNullOrWhiteSpace(data.MapDatum) ? "WGS-84" : data.MapDatum);

                    if (data.Altitude is { } alt)
                    {
                        exif.SetValue(ExifTag.GPSAltitude, ToRational(Math.Abs(alt), 100));
                        exif.SetValue(ExifTag.GPSAltitudeRef, (byte)(alt < 0 ? 1 : 0));
                    }

                    if (data.Direction is { } dir)
                    {
                        exif.SetValue(ExifTag.GPSImgDirection, ToRational(GeoMath.NormalizeDirection(dir), 100));
                        exif.SetValue(ExifTag.GPSImgDirectionRef, "T"); // T = относительно истинного севера
                    }

                    if (data.GpsTimeUtc is { } t)
                    {
                        exif.SetValue(ExifTag.GPSDateStamp, t.ToString("yyyy:MM:dd", Invariant));
                        exif.SetValue(ExifTag.GPSTimestamp, new[]
                        {
                            new Rational((uint)t.Hour, 1),
                            new Rational((uint)t.Minute, 1),
                            ToRational(t.Second + t.Millisecond / 1000.0, 1000)
                        });
                    }

                    if (!string.IsNullOrWhiteSpace(data.ProcessingMethod))
                        exif.SetValue(ExifTag.GPSProcessingMethod, new EncodedString(data.ProcessingMethod));
                }

                if (data.DateTaken is { } taken)
                    exif.SetValue(ExifTag.DateTimeOriginal, taken.ToString("yyyy:MM:dd HH:mm:ss", Invariant));

                image.Save(temp);
            }

            File.Move(temp, fullTarget, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void WriteCoordinate(ExifProfile exif, ExifTag<Rational[]> valueTag, ExifTag<string> refTag,
        double value, string positiveRef, string negativeRef)
    {
        var (d, m, s) = GeoMath.ToDms(value);
        exif.SetValue(valueTag, new[]
        {
            new Rational((uint)d, 1),
            new Rational((uint)m, 1),
            ToRational(s, 10000)
        });
        exif.SetValue(refTag, value >= 0 ? positiveRef : negativeRef);
    }

    private static Rational ToRational(double value, uint denominator)
        => new((uint)Math.Round(value * denominator), denominator);

    private static string? NullIfEmpty(string? s)
    {
        var t = s?.Trim('\0', ' ');
        return string.IsNullOrEmpty(t) ? null : t;
    }
}
