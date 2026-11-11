using System.Globalization;
using PDFMaster.Domain;

namespace PDFMaster.Core.Settings;

/// <summary>Mantém as configurações em memória e as persiste pelo <see cref="ISettingsStore"/>.</summary>
public sealed class SettingsService
{
    private readonly ISettingsStore _store;

    public SettingsService(ISettingsStore store) => _store = store;

    public AppSettings Current { get; private set; } = new();

    /// <summary>
    /// Disparado depois que uma alteração é salva. É chamado no mesmo contexto de quem pediu a
    /// alteração (a thread da interface, no aplicativo), por isso não há ConfigureAwait(false) aqui.
    /// </summary>
    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var values = await _store.LoadAllAsync(ct);
        Current = FromDictionary(values);
    }

    public async Task UpdateAsync(Action<AppSettings> change, CancellationToken ct = default)
    {
        var updated = Current.Clone();
        change(updated);
        await _store.SaveAsync(ToDictionary(updated), ct);
        Current = updated;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static bool IsValidAccent(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 7 || value[0] != '#') return false;
        for (var i = 1; i < 7; i++)
        {
            if (!Uri.IsHexDigit(value[i])) return false;
        }

        return true;
    }

    public static AppSettings FromDictionary(IReadOnlyDictionary<string, string> v)
    {
        var s = new AppSettings();
        if (v.TryGetValue("theme", out var theme) && Enum.TryParse<AppTheme>(theme, out var t)) s.Theme = t;
        if (v.TryGetValue("accent", out var accent) && IsValidAccent(accent)) s.AccentColor = accent.ToUpperInvariant();
        if (v.TryGetValue("defaultFolder", out var df) && df.Length > 0) s.DefaultOutputFolder = df;
        if (v.TryGetValue("rememberLastFolder", out var rl) && bool.TryParse(rl, out var rlb)) s.RememberLastFolder = rlb;
        if (v.TryGetValue("lastFolder", out var lf) && lf.Length > 0) s.LastFolder = lf;
        if (v.TryGetValue("overwritePolicy", out var op) && Enum.TryParse<OverwritePolicy>(op, out var opv)) s.OverwritePolicy = opv;
        if (v.TryGetValue("subfolderMode", out var sm) && Enum.TryParse<SubfolderMode>(sm, out var smv)) s.SubfolderMode = smv;
        if (v.TryGetValue("historyEnabled", out var he) && bool.TryParse(he, out var heb)) s.HistoryEnabled = heb;
        if (v.TryGetValue("ffmpegPath", out var fp) && fp.Length > 0) s.FfmpegPath = fp;
        if (v.TryGetValue("batchParallel", out var bp) && int.TryParse(bp, NumberStyles.None, CultureInfo.InvariantCulture, out var bpv) && bpv is >= 0 and <= 8)
            s.BatchParallel = bpv;
        if (v.TryGetValue("compressionProfile", out var cp) && cp.Length is > 0 and < 32 && cp.All(char.IsAsciiLetter)) s.DefaultCompressionProfile = cp;
        if (v.TryGetValue("ocrLanguages", out var ol) && ol.Length is > 0 and < 120 && ol.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '_' or '-'))
            s.OcrLanguages = ol;
        if (v.TryGetValue("imageFormat", out var imf) && imf is "Png" or "Jpeg" or "Tiff" or "Bmp") s.ImageFormat = imf;
        if (v.TryGetValue("imageDpi", out var idp) && int.TryParse(idp, NumberStyles.None, CultureInfo.InvariantCulture, out var idpv) && idpv is >= 36 and <= 1200)
            s.ImageDpi = idpv;
        if (v.TryGetValue("imageJpegQuality", out var ijq) && int.TryParse(ijq, NumberStyles.None, CultureInfo.InvariantCulture, out var ijqv) && ijqv is >= 1 and <= 100)
            s.ImageJpegQuality = ijqv;
        if (v.TryGetValue("imagePdfPageSize", out var ips) && ips is "FitImage" or "A4" or "Letter") s.ImagePdfPageSize = ips;
        if (v.TryGetValue("imagePdfMarginMm", out var imm) && int.TryParse(imm, NumberStyles.None, CultureInfo.InvariantCulture, out var immv) && immv is >= 0 and <= 100)
            s.ImagePdfMarginMm = immv;
        if (v.TryGetValue("ocrDpi", out var od) && int.TryParse(od, NumberStyles.None, CultureInfo.InvariantCulture, out var odv) && odv is >= 100 and <= 600)
            s.OcrDpi = odv;
        return s;
    }

    public static IReadOnlyDictionary<string, string> ToDictionary(AppSettings s) => new Dictionary<string, string>
    {
        ["theme"] = s.Theme.ToString(),
        ["accent"] = s.AccentColor ?? string.Empty,
        ["defaultFolder"] = s.DefaultOutputFolder ?? string.Empty,
        ["rememberLastFolder"] = s.RememberLastFolder.ToString(),
        ["lastFolder"] = s.LastFolder ?? string.Empty,
        ["overwritePolicy"] = s.OverwritePolicy.ToString(),
        ["subfolderMode"] = s.SubfolderMode.ToString(),
        ["historyEnabled"] = s.HistoryEnabled.ToString(),
        ["ffmpegPath"] = s.FfmpegPath ?? string.Empty,
        ["batchParallel"] = s.BatchParallel.ToString(CultureInfo.InvariantCulture),
        ["compressionProfile"] = s.DefaultCompressionProfile,
        ["ocrLanguages"] = s.OcrLanguages,
        ["ocrDpi"] = s.OcrDpi.ToString(CultureInfo.InvariantCulture),
        ["imageFormat"] = s.ImageFormat,
        ["imageDpi"] = s.ImageDpi.ToString(CultureInfo.InvariantCulture),
        ["imageJpegQuality"] = s.ImageJpegQuality.ToString(CultureInfo.InvariantCulture),
        ["imagePdfPageSize"] = s.ImagePdfPageSize,
        ["imagePdfMarginMm"] = s.ImagePdfMarginMm.ToString(CultureInfo.InvariantCulture),
    };
}
