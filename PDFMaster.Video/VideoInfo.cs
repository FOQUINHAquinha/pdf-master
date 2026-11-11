using System.Globalization;
using System.Text.RegularExpressions;

namespace PDFMaster.Video;

/// <summary>
/// Dados de um vídeo lidos pelo FFmpeg. <see cref="Width"/> e <see cref="Height"/> são as
/// dimensões gravadas no arquivo; a imagem exibida considera a proporção do pixel e a rotação.
/// </summary>
/// <param name="Container">Formato do arquivo informado pelo FFmpeg (ex.: "mov,mp4,m4a,3gp,3g2,mj2", "avi").</param>
/// <param name="SampleAspect">Proporção do pixel (1 = pixel quadrado).</param>
/// <param name="Rotation">Rotação de exibição em graus (0, 90, 180 ou 270), como gravada pelos celulares.</param>
public sealed record VideoInfo(
    string Container,
    TimeSpan? Duration,
    string Codec,
    int Width,
    int Height,
    double SampleAspect,
    double? FrameRate,
    int Rotation,
    bool HasAudio,
    string? AudioCodec,
    int? BitrateKbps)
{
    public const int TargetShortSide = 360;

    private bool Turned => Rotation is 90 or 270;

    /// <summary>Largura da imagem exibida (com a proporção do pixel e a rotação aplicadas).</summary>
    public int DisplayWidth => (int)Math.Round(Turned ? Height : Width * SampleAspect);

    public int DisplayHeight => (int)Math.Round(Turned ? Width * SampleAspect : Height);

    /// <summary>
    /// Tamanho do vídeo comprimido: o lado menor da imagem exibida fica com 360 pixels (ou menos,
    /// se o original já for menor — nunca há ampliação), a proporção é mantida e as dimensões são pares.
    /// É a mesma conta do filtro usado na conversão (<see cref="VideoCompressor.ScaleFilter"/>).
    /// </summary>
    public (int Width, int Height) TargetSize()
    {
        // Depois da rotação automática do FFmpeg, largura e altura trocam e o pixel inverte.
        double iw = Turned ? Height : Width;
        double ih = Turned ? Width : Height;
        var sar = Turned ? 1 / SampleAspect : SampleAspect;
        var dar = iw * sar / ih;
        if (dar >= 1)
        {
            var height = Even(Math.Min(TargetShortSide, ih), round: false);
            return (Even(height * dar, round: true), height);
        }

        var width = Even(Math.Min(TargetShortSide, iw * sar), round: false);
        return (width, Even(width / dar, round: true));
    }

    private static int Even(double value, bool round) =>
        2 * (int)(round ? Math.Round(value / 2, MidpointRounding.AwayFromZero) : Math.Truncate(value / 2));

    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{DisplayWidth}×{DisplayHeight}" };
            if (Duration is { } duration) parts.Add(FormatDuration(duration));
            if (FrameRate is { } fps) parts.Add(fps.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) + " qps");
            parts.Add(HasAudio ? Codec + " + " + AudioCodec : Codec + " (sem áudio)");
            return string.Join(" · ", parts);
        }
    }

    public static string FormatDuration(TimeSpan time) =>
        time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}

/// <summary>Interpreta o relatório que o FFmpeg escreve ao abrir um arquivo ("ffmpeg -i arquivo").</summary>
public static partial class VideoProbe
{
    [GeneratedRegex(@"^Input #0, (?<format>[^,]+(?:,[^, ]+)*), from ", RegexOptions.Multiline)]
    private static partial Regex InputLine();

    [GeneratedRegex(@"Duration:\s*(?<h>\d+):(?<m>\d{2}):(?<s>\d{2}(?:\.\d+)?)")]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"Duration:.*?bitrate:\s*(?<kbps>\d+)\s*kb/s")]
    private static partial Regex BitratePattern();

    [GeneratedRegex(@"^\s*Stream #\d+:\d+\S*: (?<type>Video|Audio|Subtitle|Data|Attachment): (?<rest>.*)$")]
    private static partial Regex StreamLine();

    [GeneratedRegex(@",\s*(?<w>\d{1,5})x(?<h>\d{1,5})(?=[\s,\[]|$)")]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"\[SAR (?<n>\d+):(?<d>\d+) DAR \d+:\d+\]")]
    private static partial Regex SarPattern();

    [GeneratedRegex(@",\s*(?<fps>\d+(?:\.\d+)?)(?<k>k)?\s+fps")]
    private static partial Regex FpsPattern();

    [GeneratedRegex(@"rotation of (?<deg>-?\d+(?:\.\d+)?) degrees")]
    private static partial Regex RotationPattern();

    /// <summary>
    /// Lê o relatório. Devolve null quando não há uma faixa de vídeo de verdade (só áudio, só capa
    /// de álbum etc.). Lança <see cref="VideoException"/> quando o arquivo não pôde ser aberto.
    /// </summary>
    public static VideoInfo? Parse(string report, string fileName)
    {
        var input = InputLine().Match(report);
        if (!input.Success)
        {
            if (report.Contains("Invalid data found when processing input", StringComparison.Ordinal))
                throw new VideoException($"{fileName} não é um vídeo reconhecido ou está danificado.");
            if (report.Contains("No such file or directory", StringComparison.Ordinal))
                throw new VideoException($"Arquivo não encontrado: {fileName}");
            if (report.Contains("Permission denied", StringComparison.Ordinal))
                throw new VideoException($"Sem permissão para ler {fileName}.");
            throw new VideoException($"O FFmpeg não conseguiu abrir {fileName}." + Detail(report));
        }

        var container = input.Groups["format"].Value;
        TimeSpan? duration = null;
        var d = DurationPattern().Match(report);
        if (d.Success)
        {
            duration = TimeSpan.FromHours(int.Parse(d.Groups["h"].Value, CultureInfo.InvariantCulture)) +
                       TimeSpan.FromMinutes(int.Parse(d.Groups["m"].Value, CultureInfo.InvariantCulture)) +
                       TimeSpan.FromSeconds(double.Parse(d.Groups["s"].Value, CultureInfo.InvariantCulture));
        }

        int? bitrate = null;
        var b = BitratePattern().Match(report);
        if (b.Success) bitrate = int.Parse(b.Groups["kbps"].Value, CultureInfo.InvariantCulture);

        var lines = report.Split('\n');
        string? videoRest = null;
        var rotation = 0;
        string? audioCodec = null;
        var inVideo = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var stream = StreamLine().Match(line);
            if (stream.Success)
            {
                inVideo = false;
                var rest = stream.Groups["rest"].Value;
                switch (stream.Groups["type"].Value)
                {
                    case "Video" when videoRest is null && !rest.Contains("(attached pic)", StringComparison.Ordinal):
                        videoRest = rest;
                        inVideo = true;
                        break;
                    case "Audio" when audioCodec is null:
                        audioCodec = FirstToken(rest);
                        break;
                }

                continue;
            }

            if (line.StartsWith("Output #", StringComparison.Ordinal) || line.StartsWith("Stream mapping", StringComparison.Ordinal)) break;
            if (inVideo)
            {
                var r = RotationPattern().Match(line);
                if (r.Success) rotation = NormalizeRotation(double.Parse(r.Groups["deg"].Value, CultureInfo.InvariantCulture));
            }
        }

        if (videoRest is null) return null;

        var size = SizePattern().Match(videoRest);
        if (!size.Success) throw new VideoException($"O FFmpeg não informou o tamanho da imagem de {fileName}.");
        var width = int.Parse(size.Groups["w"].Value, CultureInfo.InvariantCulture);
        var height = int.Parse(size.Groups["h"].Value, CultureInfo.InvariantCulture);
        if (width <= 0 || height <= 0) throw new VideoException($"{fileName} informa um tamanho de imagem inválido ({width}×{height}).");

        var sampleAspect = 1.0;
        var sar = SarPattern().Match(videoRest);
        if (sar.Success)
        {
            var n = double.Parse(sar.Groups["n"].Value, CultureInfo.InvariantCulture);
            var den = double.Parse(sar.Groups["d"].Value, CultureInfo.InvariantCulture);
            if (n > 0 && den > 0) sampleAspect = n / den;
        }

        double? fps = null;
        var f = FpsPattern().Match(videoRest);
        if (f.Success)
        {
            fps = double.Parse(f.Groups["fps"].Value, CultureInfo.InvariantCulture) * (f.Groups["k"].Success ? 1000 : 1);
        }

        return new VideoInfo(container, duration, FirstToken(videoRest), width, height, sampleAspect, fps, rotation,
            audioCodec is not null, audioCodec, bitrate);
    }

    /// <summary>Formatos de imagem parada que o FFmpeg também abre (uma foto não é um vídeo).</summary>
    public static bool IsStillImage(VideoInfo info) =>
        info.Container is "image2" or "png_pipe" or "jpeg_pipe" or "bmp_pipe" or "tiff_pipe" or "webp_pipe" or "pgm_pipe"
            or "ppm_pipe" or "pam_pipe" or "svg_pipe" or "jpegxl_pipe" or "qoi_pipe" or "dpx_pipe" or "exr_pipe" or "psd_pipe";

    private static int NormalizeRotation(double degrees)
    {
        var quarter = (int)Math.Round(degrees / 90.0) * 90;
        // O FFmpeg informa a rotação do "displaymatrix" no sentido anti-horário; para exibir, gira-se no sentido oposto.
        var clockwise = ((-quarter % 360) + 360) % 360;
        return clockwise;
    }

    private static string FirstToken(string rest)
    {
        var end = rest.IndexOfAny([' ', ',', '(']);
        return end < 0 ? rest.Trim() : rest[..end].Trim();
    }

    internal static string Detail(string report)
    {
        var last = report.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0 && !l.StartsWith("At least one output file", StringComparison.Ordinal));
        if (string.IsNullOrEmpty(last)) return string.Empty;
        if (last.Length > 300) last = last[..300];
        return " Detalhe: " + last;
    }
}
