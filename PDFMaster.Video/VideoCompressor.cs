using System.Diagnostics;
using System.Globalization;
using System.Text;
using PDFMaster.PdfEngine;

namespace PDFMaster.Video;

/// <param name="Fraction">De 0 a 1.</param>
/// <param name="Speed">Velocidade em relação ao tempo real (2 = duas vezes mais rápido que assistir).</param>
public sealed record VideoProgress(double Fraction, TimeSpan Position, TimeSpan? Duration, double? Speed);

public sealed record VideoResult(ProducedFile File, VideoInfo Source, VideoInfo Output, long SourceBytes)
{
    /// <summary>Redução de tamanho (0,8 = 80% menor). Negativa quando o resultado ficou maior.</summary>
    public double Saving => SourceBytes <= 0 ? 0 : 1 - File.Bytes / (double)SourceBytes;
}

/// <summary>
/// Converte qualquer vídeo que o FFmpeg consiga abrir para MP4 (H.264 + AAC) em 360p: o lado menor
/// da imagem fica com 360 pixels, sem ampliar vídeos menores, mantendo a proporção, a orientação
/// e no máximo 30 quadros por segundo. O original nunca é alterado e o resultado só aparece na
/// pasta de destino quando está completo.
/// </summary>
public sealed class VideoCompressor
{
    public const string OperationName = "Comprimir vídeo";
    public const string OutputSuffix = "_360p";
    public const int AudioBitrateKbps = 96;
    public const int MaxFrameRate = 30;

    /// <summary>Extensões oferecidas no diálogo; qualquer outro arquivo também é aceito se o FFmpeg conseguir abri-lo.</summary>
    public static readonly IReadOnlyList<string> Extensions =
    [
        ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".webm", ".wmv", ".asf", ".flv", ".f4v", ".mpg", ".mpeg", ".m2v",
        ".vob", ".ts", ".mts", ".m2ts", ".3gp", ".3g2", ".ogv", ".mxf", ".dv", ".divx", ".rm", ".rmvb", ".gif",
    ];

    /// <summary>
    /// Filtro de redimensionamento. Usa a proporção exibida (dar) para que vídeos anamórficos e
    /// verticais fiquem corretos; a rotação dos celulares já foi aplicada pelo FFmpeg antes dele.
    /// </summary>
    public static readonly string ScaleFilter = BuildScaleFilter(VideoInfo.TargetShortSide);

    private static string BuildScaleFilter(int side)
    {
        // Lado menor (par, sem ampliar) e o outro lado calculado a partir dele pela proporção exibida.
        var landscapeShort = $"2*trunc(min({side},ih)/2)";
        var portraitShort = $"2*trunc(min({side},iw*sar)/2)";
        return "scale=" +
               $"w='if(gte(dar,1),2*round({landscapeShort}*dar/2),{portraitShort})':" +
               $"h='if(gte(dar,1),{landscapeShort},2*round({portraitShort}/dar/2))':" +
               "flags=bicubic,setsar=1";
    }

    private readonly FfmpegRuntime _runtime;
    private readonly OutputPlanner _planner;

    public VideoCompressor(FfmpegRuntime runtime, OutputPlanner planner)
    {
        _runtime = runtime;
        _planner = planner;
    }

    public FfmpegRuntime Runtime => _runtime;

    public static bool HasVideoExtension(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Lê duração, tamanho e faixas do vídeo. Lança <see cref="VideoException"/> se não for um vídeo.</summary>
    public async Task<VideoInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        var ffmpeg = await _runtime.GetAsync(ct).ConfigureAwait(false);
        return await ProbeAsync(ffmpeg.Path, path, Path.GetFileName(path), ct).ConfigureAwait(false);
    }

    private static async Task<VideoInfo> ProbeAsync(string ffmpeg, string path, string displayName, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new VideoException($"Arquivo não encontrado: {full}");

        var output = await ProcessRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-nostdin", "-protocol_whitelist", "file", "-i", "file:" + full],
            TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);

        var info = VideoProbe.Parse(output.StandardError, displayName)
                   ?? throw new VideoException($"{displayName} não tem imagem de vídeo (parece ser só áudio).");
        if (VideoProbe.IsStillImage(info))
            throw new VideoException($"{displayName} é uma imagem, não um vídeo.");
        return info;
    }

    /// <summary>Comprime um vídeo e grava "nome_360p.mp4" na pasta de destino.</summary>
    public async Task<VideoResult> CompressAsync(
        string path,
        OutputTarget target,
        IProgress<VideoProgress>? progress = null,
        CancellationToken ct = default,
        ISet<string>? reserved = null)
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);
        if (!File.Exists(full)) throw new VideoException($"Arquivo não encontrado: {full}");

        var ffmpeg = await _runtime.GetAsync(ct).ConfigureAwait(false);
        var source = await ProbeAsync(ffmpeg.Path, full, name, ct).ConfigureAwait(false);
        var sourceBytes = new FileInfo(full).Length;

        var (output, overwrite) = _planner.Resolve(
            Path.GetFileNameWithoutExtension(full) + OutputSuffix + ".mp4", target, OperationName, [full], reserved);
        var folder = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(folder);

        string? temp = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".pdfmaster-tmp");
        try
        {
            await EncodeAsync(ffmpeg, full, temp, name, source.Duration, progress, ct).ConfigureAwait(false);

            var bytes = new FileInfo(temp).Length;
            if (bytes == 0) throw new VideoException($"O FFmpeg não gerou nenhum dado para {name}.");
            var result = await ProbeAsync(ffmpeg.Path, temp, name, ct).ConfigureAwait(false);
            if (!string.Equals(result.Codec, "h264", StringComparison.Ordinal))
                throw new VideoException($"O resultado de {name} não saiu em H.264 ({result.Codec}).");

            File.Move(temp, output, overwrite);
            temp = null;
            progress?.Report(new VideoProgress(1, source.Duration ?? TimeSpan.Zero, source.Duration, null));
            return new VideoResult(new ProducedFile(output, bytes, 0), source, result, sourceBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VideoException($"Não foi possível gravar o vídeo de {name}: {ex.Message}", ex);
        }
        finally
        {
            if (temp is not null) await DeleteAsync(temp).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Apaga o temporário. Logo depois de o FFmpeg ser encerrado, o Windows (ou o antivírus) ainda
    /// pode segurar o arquivo por um instante; por isso há algumas novas tentativas.
    /// </summary>
    private static async Task DeleteAsync(string path)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Argumentos completos da conversão (públicos para teste e diagnóstico).</summary>
    public static IReadOnlyList<string> BuildArguments(string encoder, string input, string output)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-progress", "pipe:1",
            // Só arquivos locais: uma lista de reprodução dentro do vídeo não pode acessar a rede.
            "-protocol_whitelist", "file",
            "-i", "file:" + input,
            // Primeira faixa de vídeo que não seja capa de álbum ("V") e, se existir, a primeira de áudio.
            "-map", "0:V:0", "-map", "0:a:0?",
            "-sn", "-dn", "-map_chapters", "-1",
            "-vf", ScaleFilter,
            "-fpsmax", MaxFrameRate.ToString(CultureInfo.InvariantCulture),
            "-c:v", encoder,
        };

        args.AddRange(encoder switch
        {
            "libx264" => ["-preset", "medium", "-crf", "28", "-maxrate", "1000k", "-bufsize", "2000k", "-profile:v", "high"],
            "libopenh264" => ["-b:v", "700k", "-maxrate", "1000k", "-profile:v", "high"],
            _ => ["-b:v", "700k"],
        });

        args.AddRange(
        [
            "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", AudioBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k", "-ac", "2",
            // Índice no início do arquivo: o vídeo começa a tocar antes de ser baixado por completo.
            "-movflags", "+faststart",
            "-f", "mp4", "-y", "file:" + output,
        ]);
        return args;
    }

    private static async Task EncodeAsync(FfmpegInfo ffmpeg, string input, string output, string name, TimeSpan? duration,
        IProgress<VideoProgress>? progress, CancellationToken ct)
    {
        using var process = new Process { StartInfo = ProcessRunner.CreateStartInfo(ffmpeg.Path, BuildArguments(ffmpeg.Encoder, input, output)) };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new VideoException("Não foi possível executar o FFmpeg: " + ex.Message, ex);
        }

        try
        {
            // A conversão não deve deixar o computador (nem a janela do programa) lentos.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        var errors = new StringBuilder();
        var stderr = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
            {
                lock (errors)
                {
                    if (errors.Length < 20_000) errors.AppendLine(line);
                }
            }
        }, CancellationToken.None);

        var stdout = Task.Run(async () =>
        {
            var position = TimeSpan.Zero;
            double? speed = null;
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
            {
                var equals = line.IndexOf('=');
                if (equals <= 0) continue;
                var key = line[..equals];
                var value = line[(equals + 1)..].Trim();
                switch (key)
                {
                    // "out_time_ms" também está em microssegundos (nome histórico do FFmpeg).
                    case "out_time_us" or "out_time_ms" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0:
                        position = TimeSpan.FromMicroseconds(us);
                        break;
                    case "speed" when value.EndsWith('x') &&
                                      double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s):
                        speed = s;
                        break;
                    case "progress":
                        var fraction = duration is { TotalSeconds: > 0 } d ? Math.Clamp(position / d, 0, 0.99) : 0;
                        progress?.Report(new VideoProgress(fraction, position, duration, speed));
                        break;
                }
            }
        }, CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessRunner.Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string report;
            lock (errors)
            {
                report = errors.ToString();
            }

            throw new VideoException(Explain(report, name));
        }
    }

    /// <summary>Traduz o erro do FFmpeg para uma mensagem compreensível.</summary>
    public static string Explain(string report, string name)
    {
        if (report.Contains("matches no streams", StringComparison.Ordinal))
            return $"{name} não tem imagem de vídeo (parece ser só áudio).";
        if (report.Contains("Invalid data found", StringComparison.Ordinal))
            return $"{name} não é um vídeo reconhecido ou está danificado.";
        if (report.Contains("No space left", StringComparison.OrdinalIgnoreCase))
            return "Não há espaço suficiente no disco da pasta de destino.";
        if (report.Contains("Permission denied", StringComparison.Ordinal))
            return "Sem permissão para ler o vídeo ou gravar na pasta de destino.";
        if (report.Contains("Protocol not on whitelist", StringComparison.Ordinal))
            return $"{name} tenta buscar conteúdo fora do computador (rede ou lista de reprodução) e não foi convertido.";
        return $"O FFmpeg não conseguiu converter {name}." + VideoProbe.Detail(report);
    }
}
