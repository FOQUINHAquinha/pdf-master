using System.IO.Pipes;
using System.Text;

namespace PDFMaster.Batch;

/// <summary>
/// O Explorador abre um processo por arquivo selecionado ao usar o menu de contexto. O primeiro
/// processo cria um pipe local (apenas do mesmo usuário), recebe os caminhos dos demais e abre uma
/// única janela com todos os arquivos; os outros terminam logo depois de enviar o seu.
/// </summary>
public static class ContextCollector
{
    private const int ConnectTimeoutMs = 1500;

    /// <summary>
    /// Devolve todos os arquivos (quando este processo é o primeiro) ou null quando os caminhos
    /// foram entregues a outro processo, que abrirá a janela.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> CollectAsync(string action, IReadOnlyList<string> files, TimeSpan quiet, string? channel = null,
        CancellationToken ct = default)
    {
        var name = "PDFMaster.Context." + (channel ?? Environment.UserName) + "." + action;

        // O próprio pipe (uma única instância) decide quem coleta: quem conseguir criá-lo.
        NamedPipeServerStream server;
        try
        {
            server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (IOException)
        {
            if (await SendAsync(name, files, ct).ConfigureAwait(false)) return null;
            // O primeiro processo terminou de coletar neste meio-tempo: este abre a sua própria janela.
            return files;
        }

        await using (server.ConfigureAwait(false))
        {
            var all = new List<string>(files);
            var deadline = DateTime.UtcNow + quiet;
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(remaining);
                try
                {
                    await server.WaitForConnectionAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break;
                }

                using (var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true))
                {
                    while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                    {
                        if (line.Length > 0 && !all.Contains(line, StringComparer.OrdinalIgnoreCase)) all.Add(line);
                    }
                }

                server.Disconnect();
                // Cada processo novo dá mais um tempo para os próximos chegarem.
                deadline = DateTime.UtcNow + quiet;
            }

            return all;
        }
    }

    private static async Task<bool> SendAsync(string name, IReadOnlyList<string> files, CancellationToken ct)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(ConnectTimeoutMs, ct).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (var file in files) await writer.WriteLineAsync(file).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
