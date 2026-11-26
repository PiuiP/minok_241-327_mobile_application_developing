using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Processing;

namespace PhotoGeoEditor.Core;

/// <summary>Делает маленькие квадратные превью для маркеров на карте (в виде data-URI).</summary>
public static class ThumbnailService
{
    public static string? GetDataUri(string path, int size = 56)
    {
        try
        {
            // TargetSize позволяет JPEG-декодеру сразу читать уменьшенную версию — заметно быстрее на больших фото.
            var options = new DecoderOptions { TargetSize = new Size(size * 2, size * 2) };
            using var image = Image.Load(options, path);

            image.Mutate(x => x
                .AutoOrient() // учесть поворот из EXIF
                .Resize(new ResizeOptions { Size = new Size(size, size), Mode = ResizeMode.Crop }));

            using var stream = new MemoryStream();
            image.SaveAsJpeg(stream);
            return "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch
        {
            return null; // нет превью — на карте будет серый квадрат
        }
    }
}
