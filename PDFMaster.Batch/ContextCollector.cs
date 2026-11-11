using System.IO.Pipes;
using System.Text;

namespace PDFMaster.Batch;

/// <summary>
/// O Explorador abre um processo por arquivo selecionado ao usar o menu de contexto. O primeiro
/// processo recebe os caminhos dos demais por um pipe local (apenas do mesmo usuário) e abre uma
/// única janela com todos os arquivos; os outros terminam logo depois de enviar o seu.
/// </summary>
public static class ContextCollector
{
    private const int ConnectTimeoutMs = 1500;

    /// <summary>
    /// Devolve todos os arquivos (quando este processo é o primeiro) ou null quando o caminho foi
    /// entregue a outro processo, que abrirá a janela.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> CollectAsync(string action, IReadOnlyList<string> files, TimeSpan quiet, string? channel = null,
        CancellationToken ct = default)
    {
        var name = "PDFMaster.Context." + (channel ?? Environment.UserName) + "." + action;
        using var mutex = new Mutex(initiallyOwned: false, @"Local\" + name, out _);
        bool owner;
        try
        {
            owner = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owner = true;
        }

        if (!owner)
        {
            if (await SendAsync(name, files, ct).ConfigureAwait(false)) return null;
            // O primeiro processo já terminou de coletar: este abre a sua própria janela.
            return files;
        }

        try
        {
            var all = new List<string>(files);
            var deadline = DateTime.UtcNow + quiet;
            while (DateTime.UtcNow < deadline)
            {
                using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(deadline - DateTime.UtcNow);
                try
                {
                    await server.WaitForConnectionAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break;
                }

                using var reader = new StreamReader(server, Encoding.UTF8);
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (line.Length > 0 && !all.Contains(line, StringComparer.OrdinalIgnoreCase)) all.Add(line);
                }

                // Cada arquivo novo dá mais um tempo para os próximos chegarem.
                deadline = DateTime.UtcNow + quiet;
            }

            return all;
        }
        finally
        {
            mutex.ReleaseMutex();
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
