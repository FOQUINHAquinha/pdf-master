namespace PDFMaster.Domain;

// ---------- Ferramentas ----------

public enum ToolCategory
{
    Organize,
    Edit,
    Convert,
    Compress,
    Security,
    Ocr,
    Compare,
    Batch,
    Video,
}

/// <summary>Descrição de uma ferramenta do catálogo.</summary>
/// <param name="PlannedPhase">Fase do plano em que a ferramenta é implementada.</param>
/// <param name="IsAvailable">Verdadeiro somente quando a ferramenta está implementada e testada.</param>
public sealed record ToolDefinition(
    string Id,
    string Name,
    string Description,
    ToolCategory Category,
    int PlannedPhase,
    bool IsAvailable,
    string Glyph,
    IReadOnlyList<string> Synonyms);

// ---------- Arquivos ----------

public enum FileKind
{
    Pdf,
    Image,
    OfficeDocument,
    Text,
    Html,
    Unsupported,
}

public enum FileProblem
{
    None,
    NotFound,
    Unreadable,
    Empty,
    InvalidPdfHeader,
    UnsupportedType,
}

public sealed record FileEntry(
    string FullPath,
    string Name,
    long SizeBytes,
    DateTime ModifiedUtc,
    FileKind Kind,
    FileProblem Problem,
    string? Sha256)
{
    public bool IsUsable => Problem == FileProblem.None;
}

// ---------- Histórico ----------

public enum OperationStatus
{
    Completed = 0,
    Failed = 1,
    Cancelled = 2,
    Skipped = 3,
}

public sealed record HistoryRecord(
    long Id,
    string FileName,
    string Operation,
    DateTime StartedUtc,
    string Result,
    string? OutputPath,
    TimeSpan Duration,
    OperationStatus Status);

public sealed record HistoryQuery(
    string? Text = null,
    string? Operation = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Limit = 500);

public sealed record RecentFile(string Path, DateTime LastOpenedUtc);

// ---------- Configurações ----------

public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>O que fazer quando o arquivo de saída já existe.</summary>
public enum OverwritePolicy
{
    /// <summary>Gera um nome novo: "arquivo (2).pdf".</summary>
    AutoRename,
    /// <summary>Mantém o arquivo existente e pula o novo.</summary>
    Skip,
    /// <summary>Substitui o arquivo existente. Exige confirmação do usuário.</summary>
    Overwrite,
}

public enum SubfolderMode
{
    None,
    ByDate,
    ByOperation,
}

public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Light;

    /// <summary>Cor de destaque personalizada (#RRGGBB) ou null para usar a do tema.</summary>
    public string? AccentColor { get; set; }

    public string? DefaultOutputFolder { get; set; }
    public bool RememberLastFolder { get; set; } = true;
    public string? LastFolder { get; set; }
    public OverwritePolicy OverwritePolicy { get; set; } = OverwritePolicy.AutoRename;
    public SubfolderMode SubfolderMode { get; set; } = SubfolderMode.None;
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>ffmpeg.exe escolhido pelo usuário; null usa o que vem com o programa.</summary>
    public string? FfmpegPath { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

// ---------- Documentos PDF ----------

/// <summary>Tamanho da página em pontos (1/72 pol.), como gravado no arquivo, e rotação em graus.</summary>
public sealed record PdfPageInfo(double WidthPt, double HeightPt, int Rotation);

/// <param name="PageIndex">Página de destino, começando em 0.</param>
/// <param name="Level">Nível na árvore de marcadores; 0 = raiz.</param>
public sealed record PdfBookmark(string Title, int PageIndex, int Level);

public sealed record PdfDocumentInfo(
    string Path,
    long SizeBytes,
    IReadOnlyList<PdfPageInfo> Pages,
    IReadOnlyList<PdfBookmark> Bookmarks,
    string? Title)
{
    public int PageCount => Pages.Count;
}

/// <summary>
/// Uma página do documento de saída: cópia de uma página de origem (com rotação adicional
/// opcional) ou uma página em branco.
/// </summary>
public sealed record PageInstruction(string? SourcePath, int PageIndex, int Rotation, double BlankWidthPt, double BlankHeightPt)
{
    public bool IsBlank => SourcePath is null;

    public static PageInstruction From(string sourcePath, int pageIndex, int rotation = 0) =>
        new(sourcePath, pageIndex, rotation, 0, 0);

    /// <summary>Página em branco; o padrão é A4 (595 × 842 pt).</summary>
    public static PageInstruction Blank(double widthPt = 595, double heightPt = 842) =>
        new(null, -1, 0, widthPt, heightPt);
}

public sealed record OutputBookmark(string Title, int PageIndex);
