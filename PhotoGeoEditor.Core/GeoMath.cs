using System.Globalization;

namespace PhotoGeoEditor.Core;

/// <summary> функции для координат (проверка, нормализация, градусы/минуты/секунды).</summary>
public static class GeoMath
{
    public static bool IsValidLatitude(double value) => value is >= -90 and <= 90;

    public static bool IsValidLongitude(double value) => value is >= -180 and <= 180;

    /// <summary>Приводит азимут к диапазону [0; 360).</summary>
    public static double NormalizeDirection(double degrees)
    {
        var d = degrees % 360.0;
        return d < 0 ? d + 360.0 : d;
    }

    /// <summary>Приводит долготу к диапазону [-180; 180] (нужно, когда маркер утащили за край карты).</summary>
    public static double NormalizeLongitude(double degrees)
    {
        if (IsValidLongitude(degrees)) return degrees;
        var d = (degrees + 180.0) % 360.0;
        if (d < 0) d += 360.0;
        return d - 180.0;
    }

    /// <summary>Десятичные градусы -> (градусы, минуты, секунды). Знак отбрасывается, он хранится отдельно (N/S, E/W).</summary>
    public static (int Degrees, int Minutes, double Seconds) ToDms(double value)
    {
        var abs = Math.Abs(value);
        var degrees = (int)Math.Floor(abs);
        var minutesFull = (abs - degrees) * 60.0;
        var minutes = (int)Math.Floor(minutesFull);
        var seconds = (minutesFull - minutes) * 60.0;

        // защита от результата вида 59.99999 -> 60
        if (Math.Round(seconds, 4) >= 60.0)
        {
            seconds = 0;
            minutes++;
        }
        if (minutes >= 60)
        {
            minutes = 0;
            degrees++;
        }
        return (degrees, minutes, seconds);
    }

    public static double FromDms(double degrees, double minutes, double seconds)
        => degrees + minutes / 60.0 + seconds / 3600.0;

    /// <summary>Строка вида: 55 deg 45' 14.04" N (только ASCII, чтобы не ломать консоль).</summary>
    public static string FormatDms(double value, bool isLatitude)
    {
        var (d, m, s) = ToDms(value);
        var hemisphere = isLatitude ? (value >= 0 ? "N" : "S") : (value >= 0 ? "E" : "W");
        return string.Create(CultureInfo.InvariantCulture, $"{d} deg {m}' {s:F2}\" {hemisphere}");
    }
}
