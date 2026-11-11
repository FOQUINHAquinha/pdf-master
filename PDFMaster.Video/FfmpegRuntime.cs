using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace PDFMaster.Video;

/// <summary>Erro de uma operação de vídeo, com mensagem pronta para o usuário.</summary>
public sealed class VideoException : Exception
{
    public VideoException(string message) : base(message)
    {
    }

    public VideoException(string message, Exception inner) : base(message, inner)
    {
    }
}

public enum FfmpegSource
{
    /// <summary>O FFmpeg que vem dentro do programa.</summary>
    Bundled,

    /// <summary>Escolhido pelo usuário em "Localizar ffmpeg.exe…".</summary>
    Custom,

    /// <summary>Encontrado no computador (PATH, winget, Chocolatey, Scoop, ao lado do programa).</summary>
    System,
}

/// <summary>Um FFmpeg verificado e pronto para uso.</summary>
/// <param name="Encoder">Codificador H.264 que será usado (libx264, libopenh264 ou h264_mf).</param>
public sealed record FfmpegInfo(string Path, string Version, string Encoder, FfmpegSource Source)
{
    public string Description => Source switch
    {
        FfmpegSource.Bundled => $"FFmpeg {Version} incluído no programa (codificador {Encoder})",
        FfmpegSource.Custom => $"FFmpeg {Version} escolhido por você (codificador {Encoder}) — {Path}",
        _ => $"FFmpeg {Version} instalado no computador (codificador {Encoder}) — {Path}",
    };
}

/// <summary>
/// Localiza o FFmpeg usado pela compressão de vídeo. A ordem é: o ffmpeg.exe escolhido pelo
/// usuário, o que vem dentro do programa (extraído na primeira utilização para a pasta de dados)
/// e, por último, um FFmpeg já instalado no computador. Nada é baixado da internet.
/// </summary>
public sealed class FfmpegRuntime
{
    public const string ResourceAssemblyName = "PDFMaster.Video.Ffmpeg";
    private const string ExeResource = "ffmpeg.exe.gz";
    private const string ManifestResource = "ffmpeg.manifest.txt";
    private const string LicenseResource = "ffmpeg.LICENSE.txt";

    /// <summary>Codificadores H.264 aceitos, do preferido para o menos preferido.</summary>
    public static readonly IReadOnlyList<string> PreferredEncoders = ["libx264", "libopenh264", "h264_mf"];

    public const string MissingMessage =
        "O FFmpeg, usado para converter vídeos, não foi encontrado. Esta cópia do PDF Master não traz o FFmpeg " +
        "embutido; use \"Localizar ffmpeg.exe…\" para indicar um FFmpeg instalado no computador.";

    private readonly string _runtimeRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FfmpegInfo? _found;
    private string? _problem;
    private bool _searched;
    private string? _customPath;

    /// <param name="dataDirectory">Pasta de dados do programa (ex.: %LOCALAPPDATA%\PDFMaster).</param>
    public FfmpegRuntime(string dataDirectory)
    {
        _runtimeRoot = Path.Combine(dataDirectory, "video-runtime");
    }

    /// <summary>ffmpeg.exe escolhido pelo usuário (tem prioridade sobre os demais).</summary>
    public string? CustomPath
    {
        get => _customPath;
        set
        {
            if (string.Equals(_customPath, value, StringComparison.OrdinalIgnoreCase)) return;
            _customPath = string.IsNullOrWhiteSpace(value) ? null : value;
            _searched = false;
        }
    }

    /// <summary>Pasta onde o FFmpeg embutido é extraído.</summary>
    public string RuntimeDirectory => _runtimeRoot;

    /// <summary>Procura de novo na próxima chamada (por exemplo, depois que o usuário instalou o FFmpeg).</summary>
    public void Refresh() => _searched = false;

    /// <summary>Devolve o FFmpeg pronto para uso ou lança <see cref="VideoException"/> explicando o que falta.</summary>
    public async Task<FfmpegInfo> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_searched)
            {
                (_found, _problem) = await Task.Run(() => LocateAsync(ct), ct).ConfigureAwait(false);
                _searched = true;
            }

            return _found ?? throw new VideoException(_problem ?? MissingMessage);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Verifica um ffmpeg.exe escolhido pelo usuário, sem alterar o que está em uso.</summary>
    public Task<FfmpegInfo> ValidateAsync(string path, CancellationToken ct = default) =>
        ValidateAsync(path, FfmpegSource.Custom, ct);

    private async Task<(FfmpegInfo?, string?)> LocateAsync(CancellationToken ct)
    {
        string? customProblem = null;
        if (_customPath is { } custom)
        {
            try
            {
                return (await ValidateAsync(custom, FfmpegSource.Custom, ct).ConfigureAwait(false), null);
            }
            catch (VideoException ex)
            {
                customProblem = ex.Message;
            }
        }

        string? bundledProblem = null;
        try
        {
            var bundled = ExtractBundled();
            if (bundled is not null)
                return (await ValidateAsync(bundled, FfmpegSource.Bundled, ct).ConfigureAwait(false), null);
        }
        catch (VideoException ex)
        {
            bundledProblem = ex.Message;
        }

        foreach (var candidate in SystemCandidates())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return (await ValidateAsync(candidate, FfmpegSource.System, ct).ConfigureAwait(false), null);
            }
            catch (VideoException)
            {
                // Próximo candidato.
            }
        }

        var problem = customProblem ?? bundledProblem ?? MissingMessage;
        return (null, problem);
    }

    private static IEnumerable<string> SystemCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string?> Raw()
        {
            yield return Environment.GetEnvironmentVariable("PDFMASTER_FFMPEG");

            var appFolder = AppContext.BaseDirectory;
            yield return Path.Combine(appFolder, "ffmpeg.exe");
            yield return Path.Combine(appFolder, "ffmpeg", "ffmpeg.exe");
            yield return Path.Combine(appFolder, "ffmpeg", "bin", "ffmpeg.exe");

            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string? candidate;
                try
                {
                    candidate = Path.Combine(folder.Trim().Trim('"'), "ffmpeg.exe");
                }
                catch (ArgumentException)
                {
                    candidate = null;
                }

                yield return candidate;
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length > 0) yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (common.Length > 0) yield return Path.Combine(common, "chocolatey", "bin", "ffmpeg.exe");
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length > 0) yield return Path.Combine(profile, "scoop", "shims", "ffmpeg.exe");
        }

        foreach (var path in Raw())
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (seen.Add(full) && File.Exists(full)) yield return full;
        }
    }

    private static async Task<FfmpegInfo> ValidateAsync(string path, FfmpegSource source, CancellationToken ct)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new VideoException($"Caminho inválido para o FFmpeg: {path}", ex);
        }

        var fileName = Path.GetFileName(full);
        if (!fileName.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new VideoException("Escolha o arquivo ffmpeg.exe.");
        }

        if (!File.Exists(full)) throw new VideoException($"O FFmpeg não foi encontrado em {full}.");

        ProcessOutput version;
        try
        {
            version = await ProcessRunner.RunAsync(full, ["-hide_banner", "-version"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        }
        catch (VideoException ex)
        {
            throw new VideoException($"{full} não pôde ser executado: {ex.Message}", ex);
        }

        var firstLine = version.StandardOutput.Split('\n', 2)[0].Trim();
        if (!firstLine.StartsWith("ffmpeg version ", StringComparison.Ordinal))
            throw new VideoException($"{full} não parece ser o FFmpeg.");
        var versionText = firstLine["ffmpeg version ".Length..].Split(' ', 2)[0];

        var encoders = await ProcessRunner.RunAsync(full, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        var available = ParseEncoders(encoders.StandardOutput);
        var encoder = PreferredEncoders.FirstOrDefault(available.Contains) ??
                      throw new VideoException($"O FFmpeg em {full} não tem um codificador H.264 (libx264, libopenh264 ou h264_mf).");
        if (!available.Contains("aac"))
            throw new VideoException($"O FFmpeg em {full} não tem o codificador de áudio AAC.");

        return new FfmpegInfo(full, versionText, encoder, source);
    }

    /// <summary>Nomes dos codificadores na saída de "ffmpeg -encoders" (linhas " V....D nome  descrição").</summary>
    public static IReadOnlySet<string> ParseEncoders(string output)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var started = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("------", StringComparison.Ordinal))
            {
                started = true;
                continue;
            }

            if (!started) continue;
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6) names.Add(parts[1]);
        }

        return names;
    }

    // ---------- FFmpeg embutido ----------

    /// <summary>Extrai o FFmpeg embutido (se houver) e devolve o caminho; null quando o programa foi compilado sem ele.</summary>
    private string? ExtractBundled()
    {
        Assembly assembly;
        try
        {
            assembly = Assembly.Load(new AssemblyName(ResourceAssemblyName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }

        string manifestText;
        using (var manifestStream = assembly.GetManifestResourceStream(ManifestResource))
        {
            if (manifestStream is null) return null;
            using var reader = new StreamReader(manifestStream, Encoding.UTF8);
            manifestText = reader.ReadToEnd();
        }

        var manifest = ParseManifest(manifestText);
        if (!manifest.TryGetValue("sha256", out var sha) || sha.Length != 64 || !sha.All(Uri.IsHexDigit))
            throw new VideoException("O FFmpeg incluído no programa está sem a identificação esperada.");
        sha = sha.ToLowerInvariant();

        var folder = Path.Combine(_runtimeRoot, "ffmpeg-" + sha[..16]);
        var exe = Path.Combine(folder, "ffmpeg.exe");
        try
        {
            if (!File.Exists(exe) || !HashMatches(exe, sha))
            {
                using var compressed = assembly.GetManifestResourceStream(ExeResource) ??
                                       throw new VideoException("O FFmpeg incluído no programa não foi encontrado nos recursos.");
                Directory.CreateDirectory(folder);
                var temp = exe + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
                    using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        gzip.CopyTo(file);
                    }

                    if (!HashMatches(temp, sha))
                        throw new VideoException("O FFmpeg incluído no programa está corrompido (a verificação SHA-256 falhou).");
                    File.Move(temp, exe, overwrite: true);
                }
                finally
                {
                    TryDelete(temp);
                }
            }

            using (var license = assembly.GetManifestResourceStream(LicenseResource))
            {
                var licensePath = Path.Combine(folder, "LICENSE.txt");
                if (license is not null && !File.Exists(licensePath))
                {
                    using var file = File.Create(licensePath);
                    license.CopyTo(file);
                }
            }

            File.WriteAllText(Path.Combine(folder, "ORIGEM.txt"), manifestText, Encoding.UTF8);
            RemoveOldVersions(folder);
            return exe;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new VideoException("Não foi possível preparar o FFmpeg na pasta de dados: " + ex.Message, ex);
        }
    }

    public static IReadOnlyDictionary<string, string> ParseManifest(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        return values;
    }

    private static bool HashMatches(string path, string expectedLowerHex)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return string.Equals(hash, expectedLowerHex, StringComparison.Ordinal);
    }

    private void RemoveOldVersions(string current)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(_runtimeRoot, "ffmpeg-*"))
            {
                if (string.Equals(folder, current, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Em uso por outra instância; fica para a próxima.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed record ProcessOutput(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Executa o FFmpeg sem janela, capturando a saída, com tempo limite e cancelamento.</summary>
internal static class ProcessRunner
{
    public static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    public static async Task<ProcessOutput> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process { StartInfo = CreateStartInfo(executable, arguments) };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new VideoException(ex.Message, ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            ct.ThrowIfCancellationRequested();
            throw new VideoException($"O FFmpeg não respondeu em {timeout.TotalSeconds:0} segundos.");
        }

        return new ProcessOutput(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    public static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // O processo já terminou.
        }
    }
}
