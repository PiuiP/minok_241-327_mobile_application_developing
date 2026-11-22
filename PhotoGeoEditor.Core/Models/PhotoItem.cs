namespace PhotoGeoEditor.Core;

/// <summary>
/// Одна фотография в программе: путь к файлу + изменяемые геоданные.
/// Хранит исходные значения из EXIF (Original), чтобы знать, что изменено, и уметь откатывать правки.
/// </summary>
public sealed class PhotoItem : ObservableObject
{
    private double? _latitude;
    private double? _longitude;
    private double? _altitude;
    private double? _direction;
    private DateTime? _gpsTimeUtc;
    private string? _processingMethod;
    private string? _mapDatum;

    public PhotoItem(string filePath, GeoData original)
    {
        FilePath = Path.GetFullPath(filePath);
        Original = original;
        ApplyGeo(original);
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath);

    /// <summary>Значения, которые сейчас записаны в файле.</summary>
    public GeoData Original { get; private set; }

    public DateTime? DateTaken => Original.DateTaken;

    public double? Latitude
    {
        get => _latitude;
        set { if (SetField(ref _latitude, value)) NotifyDerived(); }
    }

    public double? Longitude
    {
        get => _longitude;
        set { if (SetField(ref _longitude, value)) NotifyDerived(); }
    }

    public double? Altitude
    {
        get => _altitude;
        set { if (SetField(ref _altitude, value)) NotifyDerived(); }
    }

    public double? Direction
    {
        get => _direction;
        set { if (SetField(ref _direction, value)) NotifyDerived(); }
    }

    public DateTime? GpsTimeUtc
    {
        get => _gpsTimeUtc;
        set { if (SetField(ref _gpsTimeUtc, value)) NotifyDerived(); }
    }

    public string? ProcessingMethod
    {
        get => _processingMethod;
        set { if (SetField(ref _processingMethod, value)) NotifyDerived(); }
    }

    public string? MapDatum
    {
        get => _mapDatum;
        set { if (SetField(ref _mapDatum, value)) NotifyDerived(); }
    }

    public bool HasPosition => _latitude.HasValue && _longitude.HasValue;

    /// <summary>Текущие значения отличаются от тех, что записаны в файле.</summary>
    public bool IsModified => !ToGeoData().Equals(Original);

    /// <summary>Переместить фото в новую точку (вызывается, когда маркер перетащили мышкой).</summary>
    public void MoveTo(double latitude, double longitude)
    {
        Latitude = Math.Round(Math.Clamp(latitude, -90, 90), 6);
        Longitude = Math.Round(GeoMath.NormalizeLongitude(longitude), 6);
    }

    /// <summary>Убрать GPS-данные (в файл попадёт после записи).</summary>
    public void ClearGps()
    {
        Latitude = null;
        Longitude = null;
        Altitude = null;
        Direction = null;
        GpsTimeUtc = null;
        ProcessingMethod = null;
        MapDatum = null;
    }

    /// <summary>Откатить правки к значениям из файла.</summary>
    public void Revert() => ApplyGeo(Original);

    /// <summary>Выставить текущие значения (не трогает Original) — используется при загрузке состояния.</summary>
    public void ApplyGeo(GeoData data)
    {
        Latitude = data.Latitude;
        Longitude = data.Longitude;
        Altitude = data.Altitude;
        Direction = data.Direction;
        GpsTimeUtc = data.GpsTimeUtc;
        ProcessingMethod = data.ProcessingMethod;
        MapDatum = data.MapDatum;
    }

    /// <summary>Вызывается после успешной записи в сам файл: текущие значения становятся «исходными».</summary>
    public void AcceptSaved()
    {
        Original = ToGeoData();
        NotifyDerived();
    }

    public GeoData ToGeoData() => new()
    {
        Latitude = _latitude,
        Longitude = _longitude,
        Altitude = _altitude,
        Direction = _direction,
        GpsTimeUtc = _gpsTimeUtc,
        ProcessingMethod = _processingMethod,
        MapDatum = _mapDatum,
        DateTaken = Original.DateTaken
    };

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(HasPosition));
    }
}
