using System.Diagnostics;
using System.Text;
using PDFMaster.Core.Files;
using PDFMaster.Domain;
using PDFMaster.PdfEngine;
using PDFMaster.Video;
using Xunit;

namespace PDFMaster.Tests;

/// <summary>Vídeos de teste gerados pelo próprio FFmpeg (padrão de cores em movimento e um tom de áudio).</summary>
public static class VideoSamples
{
    public static readonly FfmpegRuntime Runtime = new(Path.Combine(Path.GetTempPath(), "pdfm-tests-video-runtime"));

    /// <summary>FFmpeg disponível; null quando este computador não tem (falha no CI, onde ele é obrigatório).</summary>
    public static async Task<FfmpegInfo?> FfmpegAsync()
    {
        try
        {
            return await Runtime.GetAsync();
        }
        catch (VideoException ex)
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("PDFMASTER_REQUIRE_FFMPEG"), "1", StringComparison.Ordinal),
                "O FFmpeg deveria estar incorporado neste ambiente de teste: " + ex.Message);
            return null;
        }
    }

    /// <summary>Cria um vídeo com o codificador e o contêiner pedidos.</summary>
    /// <param name="audioCodec">null = sem áudio.</param>
    /// <param name="extra">Argumentos de saída adicionais (ex.: filtros).</param>
    public static async Task<string> CreateAsync(string path, string videoCodec, string? audioCodec, int width, int height,
        double seconds = 2, int rate = 30, params string[] extra)
    {
        var ffmpeg = await Runtime.GetAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate={rate}:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        };
        if (audioCodec is not null)
        {
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}"]);
        }

        args.AddRange(["-c:v", videoCodec]);
        if (videoCodec is "mpeg4" or "mpeg2video" or "wmv2" or "flv1" or "h263" or "mjpeg") args.AddRange(["-q:v", "3"]);
        if (videoCodec == "mjpeg") args.AddRange(["-pix_fmt", "yuvj420p"]);
        if (audioCodec is not null) args.AddRange(["-c:a", audioCodec]);
        else args.Add("-an");
        args.AddRange(extra);
        args.Add(path);

        await RunAsync(ffmpeg.Path, args);
        Assert.True(File.Exists(path) && new FileInfo(path).Length > 1000, "amostra não gerada: " + path);
        return path;
    }

    public static async Task RunAsync(string exe, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, "ffmpeg falhou: " + await stderr);
    }
}

public class VideoProbeTests
{
    private const string Landscape = """
        Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'file:C:\v\ferias.mp4':
          Metadata:
            major_brand     : isom
          Duration: 00:01:23.45, start: 0.000000, bitrate: 4321 kb/s
          Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, bt709, progressive), 1920x1080 [SAR 1:1 DAR 16:9], 4190 kb/s, 29.97 fps, 29.97 tbr, 30k tbn (default)
              Metadata:
                handler_name    : VideoHandler
          Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 128 kb/s (default)
        At least one output file must be specified
        """;

    private const string PhonePortrait = """
        Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'file:C:\v\celular.mp4':
          Duration: 00:00:10.00, start: 0.000000, bitrate: 15000 kb/s
          Stream #0:0[0x1](eng): Video: hevc (Main) (hvc1 / 0x31637668), yuv420p(tv, bt709), 1920x1080, 14800 kb/s, 60 fps, 60 tbr, 600 tbn (default)
              Metadata:
                creation_time   : 2026-05-01T10:00:00.000000Z
              Side data:
                displaymatrix: rotation of -90.00 degrees
          Stream #0:1[0x2](eng): Audio: aac (LC) (mp4a / 0x6134706D), 44100 Hz, mono, fltp, 96 kb/s (default)
        At least one output file must be specified
        """;

    private const string AudioWithCover = """
        Input #0, mp3, from 'file:C:\m\musica.mp3':
          Duration: 00:03:00.00, start: 0.025057, bitrate: 320 kb/s
          Stream #0:0: Audio: mp3 (mp3float), 44100 Hz, stereo, fltp, 320 kb/s
          Stream #0:1: Video: mjpeg (Baseline), yuvj420p(pc, bt470bg/unknown/unknown), 600x600 [SAR 1:1 DAR 1:1], 90k tbr, 90k tbn (attached pic)
        At least one output file must be specified
        """;

    [Fact]
    public void Reads_size_duration_frame_rate_and_audio()
    {
        var info = VideoProbe.Parse(Landscape, "ferias.mp4")!;
        Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", info.Container);
        Assert.Equal(TimeSpan.FromSeconds(83.45), info.Duration);
        Assert.Equal(("h264", 1920, 1080), (info.Codec, info.Width, info.Height));
        Assert.Equal(1.0, info.SampleAspect);
        Assert.Equal(29.97, info.FrameRate);
        Assert.Equal(0, info.Rotation);
        Assert.True(info.HasAudio);
        Assert.Equal("aac", info.AudioCodec);
        Assert.Equal(4321, info.BitrateKbps);
        Assert.Equal((640, 360), info.TargetSize());
        Assert.Equal("1:23", VideoInfo.FormatDuration(info.Duration!.Value));
    }

    [Fact]
    public void Phone_rotation_turns_the_video_upright()
    {
        var info = VideoProbe.Parse(PhonePortrait, "celular.mp4")!;
        Assert.Equal(90, info.Rotation);
        Assert.Equal((1080, 1920), (info.DisplayWidth, info.DisplayHeight));
        Assert.Equal((360, 640), info.TargetSize());
        Assert.Equal(60, info.FrameRate);
        Assert.Equal("hevc", info.Codec);
    }

    [Fact]
    public void Cover_art_is_not_a_video_and_errors_are_explained()
    {
        Assert.Null(VideoProbe.Parse(AudioWithCover, "musica.mp3"));

        var invalid = Assert.Throws<VideoException>(() =>
            VideoProbe.Parse("file:C:\\x\\falso.mp4: Invalid data found when processing input\n", "falso.mp4"));
        Assert.Contains("não é um vídeo reconhecido", invalid.Message);

        var still = VideoProbe.Parse("Input #0, png_pipe, from 'file:a.png':\n  Duration: N/A, bitrate: N/A\n  Stream #0:0: Video: png, rgb24(pc), 800x600, 25 fps, 25 tbr, 25 tbn\n", "a.png")!;
        Assert.True(VideoProbe.IsStillImage(still));
        Assert.Null(still.Duration);
    }

    [Theory]
    [InlineData(1920, 1080, 1.0, 0, 640, 360)]
    [InlineData(1280, 720, 1.0, 0, 640, 360)]
    [InlineData(1080, 1920, 1.0, 0, 360, 640)]
    [InlineData(1920, 1080, 1.0, 90, 360, 640)]
    [InlineData(1920, 1080, 1.0, 180, 640, 360)]
    [InlineData(720, 576, 64.0 / 45, 0, 640, 360)]   // DVD PAL 16:9 (pixels não quadrados)
    [InlineData(1440, 1080, 4.0 / 3, 0, 640, 360)]   // HDV
    [InlineData(640, 480, 1.0, 0, 480, 360)]
    [InlineData(854, 480, 1.0, 0, 640, 360)]
    [InlineData(320, 240, 1.0, 0, 320, 240)]          // menor que 360p: não é ampliado
    [InlineData(352, 288, 1.0, 0, 352, 288)]
    [InlineData(1000, 1000, 1.0, 0, 360, 360)]
    [InlineData(321, 241, 1.0, 0, 320, 240)]          // dimensões sempre pares
    public void Target_size_keeps_the_proportion_with_the_short_side_at_360(int w, int h, double sar, int rotation, int ew, int eh)
    {
        var info = new VideoInfo("x", TimeSpan.FromSeconds(1), "h264", w, h, sar, 30, rotation, false, null, null);
        Assert.Equal((ew, eh), info.TargetSize());
    }

    [Fact]
    public void Encoder_list_and_manifest_are_parsed()
    {
        const string encoders = """
            Encoders:
             V..... = Video
             ------
             V....D libopenh264          OpenH264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
             V....D h264_mf              H264 via MediaFoundation (codec h264)
             A....D aac                  AAC (Advanced Audio Coding)
            """;
        var names = FfmpegRuntime.ParseEncoders(encoders);
        Assert.Equal(new[] { "aac", "h264_mf", "libopenh264" }, names.OrderBy(n => n, StringComparer.Ordinal).ToArray());

        var manifest = FfmpegRuntime.ParseManifest("version=ffmpeg version n9.0\nsha256=ABC\n\nlixo\n");
        Assert.Equal("ABC", manifest["sha256"]);
        Assert.Equal("ffmpeg version n9.0", manifest["version"]);
    }

    [Fact]
    public void Arguments_are_safe_and_produce_a_streamable_mp4()
    {
        var args = VideoCompressor.BuildArguments("libopenh264", @"C:\entrada\vídeo #1.avi", @"C:\saida\.tmp");
        var joined = string.Join(" | ", args);
        Assert.Contains("-protocol_whitelist | file", joined);
        Assert.Contains(@"-i | file:C:\entrada\vídeo #1.avi", joined);
        Assert.Contains(@"-y | file:C:\saida\.tmp", joined);
        Assert.Contains("-movflags | +faststart", joined);
        Assert.Contains("-c:v | libopenh264", joined);
        Assert.Contains("-map | 0:V:0", joined);
        Assert.Contains("-fpsmax | 30", joined);
        Assert.Contains("-f | mp4", joined);

        Assert.Contains("só áudio", VideoCompressor.Explain("Stream map '0:V:0' matches no streams.", "a.mp3"));
        Assert.Contains("espaço", VideoCompressor.Explain("Error writing trailer: No space left on device", "a.mp4"));
        Assert.Contains("Detalhe: erro qualquer", VideoCompressor.Explain("erro qualquer\n", "a.mp4"));
    }
}

/// <summary>Compressão de verdade com o FFmpeg incluído no programa.</summary>
public class VideoCompressionTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly VideoCompressor _compressor = new(VideoSamples.Runtime, new OutputPlanner(new OutputPathResolver()));

    public void Dispose() => _temp.Dispose();

    private string In(string name) => Path.Combine(_temp.Path, "entrada", name);
    private string OutFolder => Path.Combine(_temp.Path, "saida");
    private OutputTarget Target => new(OutFolder, OverwritePolicy.AutoRename, SubfolderMode.None);

    private string[] Outputs() => Directory.Exists(OutFolder) ? Directory.GetFiles(OutFolder).Select(Path.GetFileName).ToArray()! : [];

    private static (long Moov, long Mdat, string Brand) TopLevelBoxes(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[8];
        long position = 0, moov = -1, mdat = -1;
        var brand = string.Empty;
        while (position + 8 <= stream.Length)
        {
            stream.Position = position;
            stream.ReadExactly(header);
            long size = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            if (size == 1)
            {
                var large = new byte[8];
                stream.ReadExactly(large);
                size = (long)BitConverter.ToUInt64(large.Reverse().ToArray());
            }

            if (type == "ftyp")
            {
                var b = new byte[4];
                stream.ReadExactly(b);
                brand = Encoding.ASCII.GetString(b);
            }

            if (type == "moov" && moov < 0) moov = position;
            if (type == "mdat" && mdat < 0) mdat = position;
            if (size < 8) break;
            position += size;
        }

        return (moov, mdat, brand);
    }

    [Fact]
    public async Task Avi_with_audio_becomes_a_small_360p_mp4_and_the_original_is_untouched()
    {
        if (await VideoSamples.FfmpegAsync() is not { } ffmpeg) return;
        Assert.Contains(ffmpeg.Encoder, FfmpegRuntime.PreferredEncoders);
        Assert.Equal(FfmpegSource.Bundled, ffmpeg.Source);

        var input = await VideoSamples.CreateAsync(In("aula.avi"), "mpeg4", "ac3", 1280, 720, seconds: 3);
        var before = File.ReadAllBytes(input);
        var progress = new List<VideoProgress>();

        var result = await _compressor.CompressAsync(input, Target, new SyncProgress<VideoProgress>(progress.Add));

        Assert.Equal(Path.Combine(OutFolder, "aula_360p.mp4"), result.File.Path);
        Assert.Equal(new[] { "aula_360p.mp4" }, Outputs());
        Assert.Equal(before, File.ReadAllBytes(input));
        Assert.Equal(new FileInfo(result.File.Path).Length, result.File.Bytes);

        Assert.Equal(("h264", 640, 360), (result.Output.Codec, result.Output.DisplayWidth, result.Output.DisplayHeight));
        Assert.True(result.Output.HasAudio);
        Assert.Equal("aac", result.Output.AudioCodec);
        Assert.InRange(result.Output.Duration!.Value.TotalSeconds, 2.8, 3.3);
        Assert.InRange(result.Output.FrameRate!.Value, 29, 30.01);
        Assert.Equal(result.Source.TargetSize(), (result.Output.Width, result.Output.Height));
        Assert.True(result.File.Bytes < result.SourceBytes, $"{result.File.Bytes} >= {result.SourceBytes}");
        Assert.True(result.Saving > 0);

        // MP4 com o índice ("moov") antes dos dados: começa a tocar antes de terminar de baixar.
        var (moov, mdat, brand) = TopLevelBoxes(result.File.Path);
        Assert.True(moov >= 0 && mdat > moov, $"moov={moov} mdat={mdat}");
        Assert.False(string.IsNullOrWhiteSpace(brand));

        Assert.NotEmpty(progress);
        Assert.Equal(1, progress[^1].Fraction);
        Assert.All(progress, p => Assert.InRange(p.Fraction, 0, 1));
    }

    [Theory]
    [InlineData("filme.mpg", "mpeg2video", "mp2", 720, 480, 540, 360)]
    [InlineData("apresentacao.wmv", "wmv2", "wmav2", 640, 480, 480, 360)]
    [InlineData("camera.mov", "mjpeg", "pcm_s16le", 1920, 1080, 640, 360)]
    [InlineData("antigo.flv", "flv1", null, 854, 480, 640, 360)]
    [InlineData("gravacao.mkv", "ffv1", "flac", 1280, 720, 640, 360)]
    [InlineData("celular-antigo.3gp", "h263", null, 352, 288, 352, 288)]
    public async Task Many_formats_are_accepted(string name, string videoCodec, string? audioCodec, int w, int h, int ew, int eh)
    {
        if (await VideoSamples.FfmpegAsync() is null) return;
        var input = await VideoSamples.CreateAsync(In(name), videoCodec, audioCodec, w, h);

        var result = await _compressor.CompressAsync(input, Target);

        Assert.Equal(Path.GetFileNameWithoutExtension(name) + "_360p.mp4", Path.GetFileName(result.File.Path));
        Assert.Equal(("h264", ew, eh), (result.Output.Codec, result.Output.Width, result.Output.Height));
        Assert.Equal(audioCodec is not null, result.Output.HasAudio);
        Assert.Equal(result.Source.TargetSize(), (ew, eh));
    }

    [Fact]
    public async Task Vertical_rotated_anamorphic_and_small_videos_keep_their_shape()
    {
        if (await VideoSamples.FfmpegAsync() is not { } ffmpeg) return;

        var vertical = await _compressor.CompressAsync(await VideoSamples.CreateAsync(In("story.mp4"), "mpeg4", "aac", 720, 1280), Target);
        Assert.Equal((360, 640), (vertical.Output.Width, vertical.Output.Height));

        // Vídeo de celular gravado "em pé": a imagem é deitada e o arquivo manda girar 90°.
        var landscape = await VideoSamples.CreateAsync(In("deitado.mp4"), "mpeg4", "aac", 1280, 720);
        var rotated = In("celular.mp4");
        await VideoSamples.RunAsync(ffmpeg.Path,
            ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-display_rotation:v:0", "90", "-i", landscape, "-c", "copy", rotated]);
        var turned = await _compressor.CompressAsync(rotated, Target);
        Assert.Contains(turned.Source.Rotation, new[] { 90, 270 });
        Assert.Equal((360, 640), (turned.Output.Width, turned.Output.Height));
        Assert.Equal(0, turned.Output.Rotation);

        // DVD 16:9: 720×576 com pixels largos aparece como 1024×576.
        var dvd = await VideoSamples.CreateAsync(In("dvd.mkv"), "ffv1", null, 720, 576, extra: ["-vf", "setsar=64/45"]);
        var wide = await _compressor.CompressAsync(dvd, Target);
        Assert.InRange(wide.Source.SampleAspect, 1.42, 1.43);
        Assert.Equal((640, 360, 1.0), (wide.Output.Width, wide.Output.Height, wide.Output.SampleAspect));

        var small = await _compressor.CompressAsync(await VideoSamples.CreateAsync(In("pequeno.avi"), "mpeg4", null, 320, 240), Target);
        Assert.Equal((320, 240), (small.Output.Width, small.Output.Height));
        Assert.False(small.Output.HasAudio);
    }

    [Fact]
    public async Task Frame_rate_is_limited_to_30()
    {
        if (await VideoSamples.FfmpegAsync() is null) return;
        var input = await VideoSamples.CreateAsync(In("jogo.mkv"), "ffv1", null, 1280, 720, seconds: 2, rate: 60);

        var result = await _compressor.CompressAsync(input, Target);

        Assert.InRange(result.Source.FrameRate!.Value, 59.9, 60.1);
        Assert.InRange(result.Output.FrameRate!.Value, 1, 30.01);
    }

    [Fact]
    public async Task Names_with_accents_and_spaces_work_and_names_never_collide()
    {
        if (await VideoSamples.FfmpegAsync() is null) return;
        var input = await VideoSamples.CreateAsync(In("Férias em família #1 (ção).avi"), "mpeg4", "ac3", 640, 360);

        var first = await _compressor.CompressAsync(input, Target);
        var second = await _compressor.CompressAsync(input, Target);

        Assert.Equal("Férias em família #1 (ção)_360p.mp4", Path.GetFileName(first.File.Path));
        Assert.NotEqual(first.File.Path, second.File.Path);
        Assert.True(File.Exists(first.File.Path) && File.Exists(second.File.Path));
    }

    [Fact]
    public async Task Audio_images_and_other_files_are_refused_without_leaving_files()
    {
        if (await VideoSamples.FfmpegAsync() is not { } ffmpeg) return;

        var wav = In("musica.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
        await VideoSamples.RunAsync(ffmpeg.Path, ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=duration=1", wav]);
        var audio = await Assert.ThrowsAsync<VideoException>(() => _compressor.CompressAsync(wav, Target));
        Assert.Contains("não tem imagem de vídeo", audio.Message);

        var png = ImageSamples.Write(In("foto.png"), 64, 48, ImageSamples.Photo);
        var image = await Assert.ThrowsAsync<VideoException>(() => _compressor.CompressAsync(png, Target));
        Assert.Contains("é uma imagem", image.Message);

        var fake = _temp.File("falso.mp4", "isto não é um vídeo");
        var invalid = await Assert.ThrowsAsync<VideoException>(() => _compressor.CompressAsync(fake, Target));
        Assert.Contains("falso.mp4", invalid.Message);

        await Assert.ThrowsAsync<VideoException>(() => _compressor.CompressAsync(In("nao-existe.mp4"), Target));
        Assert.Empty(Outputs());
    }

    [Fact]
    public async Task Cancelling_stops_ffmpeg_and_leaves_nothing_behind()
    {
        if (await VideoSamples.FfmpegAsync() is null) return;
        var input = await VideoSamples.CreateAsync(In("longo.avi"), "mpeg4", "ac3", 1280, 720, seconds: 30);

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<VideoProgress>(p =>
        {
            if (p.Fraction > 0) cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _compressor.CompressAsync(input, Target, progress, cts.Token));
        Assert.Empty(Outputs());
    }

    [Fact]
    public async Task Ffmpeg_chosen_by_the_user_is_validated()
    {
        if (await VideoSamples.FfmpegAsync() is not { } bundled) return;

        var wrongName = await Assert.ThrowsAsync<VideoException>(() => VideoSamples.Runtime.ValidateAsync(_temp.File("programa.exe", "MZ")));
        Assert.Contains("ffmpeg.exe", wrongName.Message);
        await Assert.ThrowsAsync<VideoException>(() => VideoSamples.Runtime.ValidateAsync(_temp.File("outro\\ffmpeg.exe", "não é um programa")));
        await Assert.ThrowsAsync<VideoException>(() => VideoSamples.Runtime.ValidateAsync(Path.Combine(_temp.Path, "nada", "ffmpeg.exe")));

        var copy = Path.Combine(_temp.Path, "meu-ffmpeg", "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(bundled.Path, copy);
        var custom = await VideoSamples.Runtime.ValidateAsync(copy);
        Assert.Equal((FfmpegSource.Custom, bundled.Encoder), (custom.Source, custom.Encoder));

        // Um caminho escolhido que deixou de existir não impede o uso do FFmpeg incluído.
        var runtime = new FfmpegRuntime(Path.Combine(Path.GetTempPath(), "pdfm-tests-video-runtime")) { CustomPath = Path.Combine(_temp.Path, "sumiu", "ffmpeg.exe") };
        Assert.Equal(FfmpegSource.Bundled, (await runtime.GetAsync()).Source);
        runtime.CustomPath = copy;
        Assert.Equal(FfmpegSource.Custom, (await runtime.GetAsync()).Source);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

/// <summary>Gera o vídeo usado pela execução da ferramenta no CI (só com PDFMASTER_SMOKE_DIR).</summary>
public class SmokeSamplesVideo
{
    [Fact]
    public async Task Writes_a_video_for_the_video_tool()
    {
        var folder = Environment.GetEnvironmentVariable("PDFMASTER_SMOKE_DIR");
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (await VideoSamples.FfmpegAsync() is null) return;
        await VideoSamples.CreateAsync(Path.Combine(folder, "Video.avi"), "mpeg4", "ac3", 1280, 720, seconds: 5);
    }
}
