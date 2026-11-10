using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFMaster.Rendering;

namespace PDFMaster.App.Services;

/// <summary>
/// Miniaturas de páginas com cache em memória. As páginas são renderizadas sob demanda, em
/// segundo plano, e as mais antigas são descartadas quando o limite do cache é atingido.
/// </summary>
public sealed class ThumbnailService
{
    public const int ShortSide = 170;
    public const int LongSide = 240;
    private const int MaxCached = 400;

    private readonly IPdfRenderer _renderer;
    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Image)>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, BitmapSource Image)> _order = new();
    private readonly object _gate = new();

    public ThumbnailService(IPdfRenderer renderer) => _renderer = renderer;

    /// <summary>Converte os pixels renderizados em uma imagem imutável do WPF.</summary>
    public static BitmapSource ToBitmap(RenderedPage page, double dpi = 96)
    {
        var bitmap = BitmapSource.Create(page.Width, page.Height, dpi, dpi, PixelFormats.Bgr32, null, page.Bgra, page.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Carrega as miniaturas das páginas pedidas. <paramref name="onReady"/> é chamado na thread
    /// que iniciou a carga (a da interface) para cada página, assim que ela fica pronta.
    /// </summary>
    public async Task LoadAsync(string path, IReadOnlyList<int> pages, Action<int, ImageSource> onReady, CancellationToken ct)
    {
        var context = SynchronizationContext.Current;
        var prefix = KeyPrefix(path);
        var missing = new List<int>();

        foreach (var page in pages)
        {
            if (TryGet(prefix + page, out var cached)) onReady(page, cached);
            else missing.Add(page);
        }

        if (missing.Count == 0) return;

        await _renderer.RenderPagesAsync(path, missing, RenderSize.Fit(ShortSide, LongSide), (index, rendered) =>
        {
            var bitmap = ToBitmap(rendered);
            Store(prefix + index, bitmap);
            if (context is null)
            {
                onReady(index, bitmap);
            }
            else
            {
                context.Post(_ =>
                {
                    if (!ct.IsCancellationRequested) onReady(index, bitmap);
                }, null);
            }
        }, ct);
    }

    private static string KeyPrefix(string path)
    {
        var info = new FileInfo(path);
        return info.Exists
            ? $"{info.FullName.ToUpperInvariant()}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|"
            : path.ToUpperInvariant() + "|";
    }

    private bool TryGet(string key, out BitmapSource image)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null!;
        return false;
    }

    private void Store(string key, BitmapSource image)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var existing)) _order.Remove(existing);
            _cache[key] = _order.AddFirst((key, image));
            while (_order.Count > MaxCached)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _cache.Remove(last.Value.Key);
            }
        }
    }
}
