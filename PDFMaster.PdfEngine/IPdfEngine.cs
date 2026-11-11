using PDFMaster.Domain;

namespace PDFMaster.PdfEngine;

/// <summary>Erro de leitura ou gravação de PDF com mensagem pronta para mostrar ao usuário.</summary>
public class PdfEngineException : Exception
{
    public PdfEngineException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <param name="Bookmarks">Marcadores a criar no documento de saída.</param>
/// <param name="Title">Título gravado nos metadados; null mantém vazio.</param>
/// <param name="Overwrite">Permite substituir o arquivo de saída se ele já existir.</param>
public sealed record ComposeOptions(
    IReadOnlyList<OutputBookmark>? Bookmarks = null,
    string? Title = null,
    bool Overwrite = false);

/// <summary>
/// Camada de abstração do mecanismo de PDF. A interface e os demais módulos só conhecem este
/// contrato; a biblioteca usada (hoje PDFsharp) pode ser trocada sem alterar o restante.
///
/// Todas as ferramentas de organização (mesclar, dividir, extrair, girar, reordenar, excluir,
/// intercalar, inserir) são expressas como uma lista de <see cref="PageInstruction"/>.
/// </summary>
public interface IPdfEngine
{
    Task<PdfDocumentInfo> ReadInfoAsync(string path, CancellationToken ct = default);

    /// <summary>Monta um novo PDF com as páginas indicadas. Devolve o tamanho gravado, em bytes.</summary>
    Task<long> ComposeAsync(
        IReadOnlyList<PageInstruction> pages,
        string outputPath,
        ComposeOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>Tamanho que o documento teria se fosse gravado, sem criar arquivo.</summary>
    Task<long> MeasureAsync(IReadOnlyList<PageInstruction> pages, CancellationToken ct = default);
}
