using System.Collections.ObjectModel;

namespace PhotoGeoEditor.Core;

/// <summary>Как записывать изменения в файлы.</summary>
public enum WriteMode
{
    /// <summary>Безопасный режим: копии кладутся в подпапку geo_edited рядом с оригиналами.</summary>
    SaveCopies,

    /// <summary>Оригиналы перезаписываются.</summary>
    Overwrite
}

public sealed record SaveResult(int Saved, IReadOnlyList<string> Errors);

/// <summary>Коллекция открытых фотографий и операции над ней. Не зависит от типа интерфейса.</summary>
public sealed class PhotoLibrary
{
    public const string OutputFolderName = "geo_edited";

    public static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff" };

    private readonly IGeoMetadataService _metadata;

    public PhotoLibrary(IGeoMetadataService metadata) => _metadata = metadata;

    /// <summary>Коллекция с событием CollectionChanged — интерфейс подписывается и сам обновляется.</summary>
    public ObservableCollection<PhotoItem> Photos { get; } = new();

    public static bool IsSupported(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> EnumerateImages(string folder, bool recursive)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true };
        var marker = Path.DirectorySeparatorChar + OutputFolderName + Path.DirectorySeparatorChar;

        return Directory.EnumerateFiles(folder, "*", options)
            .Where(IsSupported)
            .Where(p => !p.Contains(marker, StringComparison.OrdinalIgnoreCase)) // не подхватывать наши копии
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Читает EXIF и создаёт PhotoItem, но в коллекцию не добавляет (можно вызывать из фонового потока).</summary>
    public PhotoItem ReadItem(string path) => new(path, _metadata.Read(path));

    public bool Add(PhotoItem item)
    {
        if (Photos.Any(p => string.Equals(p.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)))
            return false;
        Photos.Add(item);
        return true;
    }

    public bool Remove(PhotoItem item) => Photos.Remove(item);

    public void Clear() => Photos.Clear();

    /// <summary>Записывает правки в файлы согласно выбранному режиму.</summary>
    public SaveResult WriteChanges(WriteMode mode, bool onlyModified = true)
    {
        var saved = 0;
        var errors = new List<string>();

        foreach (var item in Photos.Where(p => !onlyModified || p.IsModified).ToList())
        {
            try
            {
                var target = item.FilePath;
                if (mode == WriteMode.SaveCopies)
                {
                    var dir = Path.Combine(Path.GetDirectoryName(item.FilePath)!, OutputFolderName);
                    Directory.CreateDirectory(dir);
                    target = Path.Combine(dir, item.FileName);
                }

                _metadata.Write(item.FilePath, target, item.ToGeoData());
                if (mode == WriteMode.Overwrite) item.AcceptSaved();
                saved++;
            }
            catch (Exception ex)
            {
                errors.Add($"{item.FileName}: {ex.Message}");
            }
        }

        return new SaveResult(saved, errors);
    }
}
