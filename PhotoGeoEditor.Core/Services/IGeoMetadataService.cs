namespace PhotoGeoEditor.Core;

/// <summary>Чтение и запись геоданных фотографии. Интерфейс позволяет подменить реализацию (например, в тестах).</summary>
public interface IGeoMetadataService
{
    GeoData Read(string path);

    /// <summary>
    /// Записывает геоданные в EXIF. Результат сохраняется в targetPath
    /// (может совпадать с sourcePath — тогда файл перезаписывается).
    /// </summary>
    void Write(string sourcePath, string targetPath, GeoData data);
}
