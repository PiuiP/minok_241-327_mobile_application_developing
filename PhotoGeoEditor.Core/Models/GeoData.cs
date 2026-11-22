namespace PhotoGeoEditor.Core;

/// <summary>Неизменяемый снимок геоданных одной фотографии (то, что лежит в EXIF).</summary>
public sealed record GeoData
{
    /// <summary>Широта, градусы (+ север, - юг).</summary>
    public double? Latitude { get; init; }

    /// <summary>Долгота, градусы (+ восток, - запад).</summary>
    public double? Longitude { get; init; }

    /// <summary>Высота над уровнем моря, метры.</summary>
    public double? Altitude { get; init; }

    /// <summary>Направление камеры (азимут), градусы по часовой от севера, 0..360.</summary>
    public double? Direction { get; init; }

    /// <summary>Дата и время определения координат (UTC), из GPSDateStamp + GPSTimeStamp.</summary>
    public DateTime? GpsTimeUtc { get; init; }

    /// <summary>Способ определения геопозиции (GPSProcessingMethod): GPS, WLAN, MANUAL ...</summary>
    public string? ProcessingMethod { get; init; }

    /// <summary>Система координат (GPSMapDatum), обычно WGS-84.</summary>
    public string? MapDatum { get; init; }

    /// <summary>Дата и время съёмки (DateTimeOriginal).</summary>
    public DateTime? DateTaken { get; init; }

    public bool HasPosition => Latitude.HasValue && Longitude.HasValue;
}
