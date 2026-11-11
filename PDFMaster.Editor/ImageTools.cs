using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PDFMaster.Editor;

/// <summary>Prepara imagens para o PDF: recorte e transparência aplicados nos pixels.</summary>
public static class ImageTools
{
    public static readonly IReadOnlyList<string> Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif"];

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Tamanho em pixels (primeiro quadro). Lança <see cref="InvalidDataException"/> se não for imagem.</summary>
    public static (int Width, int Height) Size(byte[] data)
    {
        var frame = Decode(data);
        return (frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>
    /// Bytes prontos para o PDFsharp: o JPG/PNG original quando não há recorte nem transparência
    /// (sem perda nem recompressão); senão, um PNG com o recorte e a transparência aplicados.
    /// </summary>
    public static byte[] Prepare(byte[] data, ImageCrop crop, double opacity)
    {
        var isJpeg = data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8;
        var isPng = data.Length > 8 && data[0] == 0x89 && data[1] == (byte)'P';
        opacity = Math.Clamp(opacity, 0.05, 1);
        if (crop.IsNone && opacity >= 0.999 && (isJpeg || isPng)) return data;

        BitmapSource source = Decode(data);
        if (!crop.IsNone)
        {
            var w = source.PixelWidth;
            var h = source.PixelHeight;
            var x = (int)Math.Round(w * Math.Clamp(crop.Left, 0, 0.95));
            var y = (int)Math.Round(h * Math.Clamp(crop.Top, 0, 0.95));
            var right = (int)Math.Round(w * (1 - Math.Clamp(crop.Right, 0, 0.95)));
            var bottom = (int)Math.Round(h * (1 - Math.Clamp(crop.Bottom, 0, 0.95)));
            source = new CroppedBitmap(source, new Int32Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y)));
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        if (opacity < 0.999)
        {
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = (byte)Math.Round(pixels[i] * opacity);
        }

        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        return EncodePng(result);
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapFrame Decode(byte[] data)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new InvalidDataException("A imagem não tem quadros.");
            return decoder.Frames[0];
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("O arquivo não é uma imagem reconhecida (PNG, JPG, BMP, TIFF ou GIF).", ex);
        }
    }
}
