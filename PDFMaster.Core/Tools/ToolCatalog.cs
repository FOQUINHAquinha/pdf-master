using PDFMaster.Domain;

namespace PDFMaster.Core.Tools;

/// <summary>
/// Catálogo de todas as ferramentas previstas na especificação. Uma ferramenta só recebe
/// <c>IsAvailable = true</c> depois de implementada e aprovada nos testes da sua fase.
/// </summary>
public sealed class ToolCatalog
{
    // Glifos da fonte "Segoe Fluent Icons" / "Segoe MDL2 Assets".
    private const string Page = "";
    private const string Copy = "";
    private const string Cut = "";
    private const string Rotate = "";
    private const string Delete = "";
    private const string Edit = "";
    private const string Picture = "";
    private const string Font = "";
    private const string Sync = "";
    private const string Shrink = "";
    private const string Lock = "";
    private const string Shield = "";
    private const string Scan = "";
    private const string Search = "";
    private const string Switch = "";
    private const string List = "";
    private const string Folder = "";
    private const string Tag = "";
    private const string Link = "";
    private const string Print = "";
    private const string Repair = "";
    private const string Add = "";
    private const string View = "\uE8A5";
    private const string VideoGlyph = "\uE714";

    /// <summary>
    /// Ferramentas implementadas e aprovadas nos testes. Só estas podem ser abertas na interface.
    /// </summary>
    public static readonly IReadOnlySet<string> Implemented = new HashSet<string>(StringComparer.Ordinal)
    {
        // Fase 2
        "view", "merge", "split", "extract-pages", "reorder", "rotate", "delete-pages", "interleave", "insert-pages",
        // Fase 3
        "pdf-to-image", "image-to-pdf", "office-to-pdf", "text-html-to-pdf", "pdf-to-word", "pdf-to-text", "compress",
        // Fase 4
        "ocr", "ocr-batch", "scan",
        // Fase 5
        "edit-text", "edit-images", "annotate", "sign-visual", "watermark", "header-footer", "stamps", "forms", "form-fields",
        // Fase 6
        "protect", "unlock", "security-info", "metadata", "verify-signature", "repair", "pdfa", "compare",
        // Fase 7
        "batch", "watch-folder", "reverse", "blank-duplicates", "crop", "booklet",
        // Extra (fora das 8 fases, pedido do autor)
        "video-compress",
    };

    public IReadOnlyList<ToolDefinition> All { get; } = Build();

    public ToolDefinition? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    public IReadOnlyList<ToolDefinition> ByCategory(ToolCategory category) =>
        All.Where(t => t.Category == category).ToList();

    public static string CategoryName(ToolCategory category) => category switch
    {
        ToolCategory.Organize => "Organizar PDF",
        ToolCategory.Edit => "Editar PDF",
        ToolCategory.Convert => "Converter PDF",
        ToolCategory.Compress => "Comprimir PDF",
        ToolCategory.Security => "Segurança",
        ToolCategory.Ocr => "OCR e digitalização",
        ToolCategory.Compare => "Comparar documentos",
        ToolCategory.Batch => "Processamento em lote",
        ToolCategory.Video => "Comprimir vídeo",
        _ => category.ToString(),
    };

    private static List<ToolDefinition> Build()
    {
        var list = new List<ToolDefinition>();
        void T(string id, string name, string description, ToolCategory category, int phase, string glyph, params string[] synonyms) =>
            list.Add(new ToolDefinition(id, name, description, category, phase, Implemented.Contains(id), glyph, synonyms));

        T("view", "Visualizador de PDF", "Abre o documento para leitura, com miniaturas, zoom e navegação.", ToolCategory.Organize, 2, View,
            "abrir pdf", "ler pdf", "ver documento", "leitor", "visualizar");

        // ----- Organizar -----
        T("merge", "Mesclar PDF", "Junta vários arquivos PDF em um único documento.", ToolCategory.Organize, 2, Copy,
            "juntar documentos", "unir pdf", "combinar pdf", "concatenar", "agrupar arquivos", "merge");
        T("split", "Dividir PDF", "Separa um PDF em vários arquivos por páginas, intervalos, tamanho ou marcadores.", ToolCategory.Organize, 2, Cut,
            "separar pdf", "cortar documento", "quebrar pdf", "partir", "split");
        T("extract-pages", "Extrair páginas", "Cria um novo PDF apenas com as páginas escolhidas.", ToolCategory.Organize, 2, Page,
            "tirar páginas", "selecionar páginas", "copiar páginas", "exportar páginas");
        T("reorder", "Reorganizar páginas", "Move, gira, duplica e exclui páginas em um editor visual de miniaturas.", ToolCategory.Organize, 2, List,
            "reordenar páginas", "mudar ordem", "ordenar páginas", "mover páginas");
        T("rotate", "Girar páginas", "Rotaciona páginas em 90, 180 ou 270 graus.", ToolCategory.Organize, 2, Rotate,
            "rotacionar", "virar página", "orientação", "rodar");
        T("delete-pages", "Remover páginas", "Exclui páginas de um documento.", ToolCategory.Organize, 2, Delete,
            "excluir páginas", "apagar páginas", "deletar páginas");
        T("interleave", "Intercalar documentos", "Alterna as páginas de dois ou mais PDFs (A1, B1, A2, B2…).", ToolCategory.Organize, 2, Switch,
            "alternar páginas", "frente e verso", "mesclar alternado");
        T("insert-pages", "Inserir páginas", "Insere páginas em branco ou de outro documento.", ToolCategory.Organize, 2, Add,
            "adicionar páginas", "página em branco", "substituir páginas", "duplicar páginas");
        T("crop", "Recortar margens das páginas", "Recorta as margens de todas as páginas (a área visível fica menor).", ToolCategory.Organize, 7, Cut,
            "cortar margens", "ajustar margens", "reduzir margens", "tamanho da página", "crop");
        T("blank-duplicates", "Páginas em branco e duplicadas", "Detecta e remove páginas vazias ou repetidas.", ToolCategory.Organize, 7, Search,
            "remover páginas vazias", "páginas repetidas", "limpar documento");
        T("booklet", "Livreto e múltiplas páginas por folha", "Monta livretos e coloca 2 ou 4 páginas por folha.", ToolCategory.Organize, 7, Print,
            "booklet", "duas páginas por folha", "quatro páginas por folha", "mosaico", "n-up", "imposição");
        T("reverse", "Inverter ordem das páginas", "Coloca as páginas do documento na ordem inversa.", ToolCategory.Organize, 7, Sync,
            "ordem inversa", "de trás para frente");
        T("bookmarks", "Marcadores, links e anexos", "Cria e edita marcadores, sumário, links e anexos.", ToolCategory.Organize, 8, Link,
            "sumário", "índice", "bookmarks", "favoritos", "anexar arquivo", "hiperlink");

        // ----- Editar -----
        T("edit-text", "Texto", "Insere texto e edita texto existente quando a estrutura do PDF permite.", ToolCategory.Edit, 5, Font,
            "escrever no pdf", "digitar", "adicionar texto", "alterar texto", "corrigir texto");
        T("edit-images", "Imagens", "Insere, move, redimensiona e substitui imagens.", ToolCategory.Edit, 5, Picture,
            "adicionar imagem", "foto", "figura", "logotipo");
        T("annotate", "Anotações", "Marca-texto, comentários, formas, setas e desenho livre.", ToolCategory.Edit, 5, Edit,
            "destacar", "grifar", "comentar", "sublinhar", "tachar", "nota adesiva", "desenhar");
        T("sign-visual", "Assinatura visual", "Desenha ou importa uma assinatura como imagem. Não é assinatura digital.", ToolCategory.Edit, 5, Edit,
            "assinar", "rubrica", "assinatura manuscrita");
        T("watermark", "Marca d'água", "Aplica marca d'água de texto ou imagem.", ToolCategory.Edit, 5, Tag,
            "marca dagua", "watermark", "fundo", "confidencial");
        T("header-footer", "Cabeçalhos e rodapés", "Adiciona texto, data e numeração de páginas.", ToolCategory.Edit, 5, Page,
            "numerar páginas", "numeração", "página x de y", "paginação");
        T("stamps", "Carimbos", "Biblioteca de carimbos prontos e personalizados.", ToolCategory.Edit, 5, Tag,
            "carimbar", "selo", "recebido", "cópia", "original");
        T("forms", "Preencher formulário", "Preenche os campos de um formulário PDF e grava uma cópia preenchida.", ToolCategory.Edit, 5, List,
            "formulário", "preencher pdf", "campos", "acroform", "preencher campos");
        T("form-fields", "Criar campos de formulário", "Cria campos de texto, caixas de seleção, opções, listas, datas e botões.", ToolCategory.Edit, 5, List,
            "criar formulário", "campo de texto", "caixa de seleção", "botão de opção", "lista suspensa", "campo de data");

        // ----- Converter -----
        T("pdf-to-image", "PDF para imagem", "Converte páginas em JPG, PNG, BMP ou TIFF.", ToolCategory.Convert, 3, Picture,
            "pdf para jpg", "pdf para png", "pdf em foto", "exportar imagem", "tiff");
        T("image-to-pdf", "Imagem para PDF", "Cria um PDF a partir de JPG, PNG, BMP ou TIFF.", ToolCategory.Convert, 3, Picture,
            "jpg para pdf", "png para pdf", "foto para pdf", "fotos em pdf");
        T("office-to-pdf", "Office para PDF", "Converte Word, Excel e PowerPoint em PDF.", ToolCategory.Convert, 3, Sync,
            "word para pdf", "docx para pdf", "excel para pdf", "xlsx", "powerpoint para pdf", "pptx");
        T("pdf-to-word", "PDF para Word", "Converte PDF em DOCX editável, com limitações de formatação informadas.", ToolCategory.Convert, 3, Sync,
            "pdf para docx", "pdf editável", "transformar em word");
        T("pdf-to-office", "PDF para Excel e PowerPoint", "Converte PDF em XLSX ou PPTX quando o conteúdo permite.", ToolCategory.Convert, 8, Sync,
            "pdf para xlsx", "pdf para planilha", "pdf para pptx", "tabela para excel");
        T("pdf-to-text", "PDF para texto e HTML", "Exporta o conteúdo em TXT ou HTML.", ToolCategory.Convert, 3, Font,
            "pdf para txt", "extrair texto", "copiar texto", "pdf para html");
        T("text-html-to-pdf", "Texto e HTML para PDF", "Cria PDF a partir de arquivos TXT ou HTML.", ToolCategory.Convert, 3, Sync,
            "txt para pdf", "html para pdf", "página web para pdf");
        T("pdfa", "Converter e validar PDF/A", "Converte para PDF/A-1b, 2b ou 3b e gera relatório de validação.", ToolCategory.Convert, 6, Shield,
            "pdf a", "arquivamento", "longo prazo", "pdfa");
        T("extract-content", "Extrair conteúdo", "Extrai imagens, texto, tabelas, anexos, fontes e metadados.", ToolCategory.Convert, 8, Folder,
            "extrair imagens", "extrair tabelas", "extrair anexos", "listar fontes");

        // ----- Comprimir -----
        T("compress", "Comprimir PDF", "Reduz o tamanho do arquivo com perfis de compressão e comparação antes/depois.", ToolCategory.Compress, 3, Shrink,
            "reduzir tamanho", "diminuir pdf", "compactar", "otimizar", "pdf menor", "deixar mais leve");
        T("grayscale", "Converter para preto e branco", "Converte as páginas para escala de cinza.", ToolCategory.Compress, 8, Shrink,
            "escala de cinza", "tons de cinza", "monocromático", "sem cor");

        // ----- Segurança -----
        T("protect", "Proteger com senha", "Define senha de abertura e restrições de impressão, cópia e edição.", ToolCategory.Security, 6, Lock,
            "colocar senha", "criptografar", "bloquear pdf", "permissões", "restringir");
        T("unlock", "Remover proteção", "Remove a senha de um PDF usando a senha fornecida por você.", ToolCategory.Security, 6, Lock,
            "tirar senha", "desbloquear", "remover senha", "descriptografar");
        T("metadata", "Metadados", "Visualiza, edita e remove título, autor, assunto e demais propriedades.", ToolCategory.Security, 6, Tag,
            "propriedades", "autor", "título", "limpar metadados", "informações do documento");
        T("security-info", "Propriedades de segurança", "Identifica documentos protegidos, permissões, assinaturas e danos estruturais.", ToolCategory.Security, 6, Shield,
            "documento protegido", "verificar pdf", "integridade", "está com senha", "analisar segurança", "permissões do pdf");
        T("verify-signature", "Verificar assinaturas digitais", "Valida criptograficamente as assinaturas digitais existentes.", ToolCategory.Security, 6, Shield,
            "assinatura digital", "certificado", "validar assinatura", "icp brasil", "conferir assinatura");
        T("repair", "Reparar PDF", "Tenta recuperar documentos danificados e relata o que foi salvo e perdido.", ToolCategory.Security, 6, Repair,
            "pdf corrompido", "arquivo danificado", "recuperar pdf", "consertar", "não abre");

        // ----- OCR -----
        T("ocr", "OCR — PDF pesquisável", "Reconhece o texto de PDFs escaneados e imagens, com camada de texto invisível.", ToolCategory.Ocr, 4, Search,
            "reconhecer texto", "pdf escaneado", "texto de imagem", "tornar pesquisável", "tesseract");
        T("ocr-batch", "OCR em lote", "Executa OCR em vários documentos com acompanhamento de progresso.", ToolCategory.Ocr, 4, List,
            "ocr em vários arquivos", "reconhecer vários");
        T("scan", "Digitalizar", "Digitaliza documentos de scanners compatíveis com WIA direto para PDF, com OCR opcional.", ToolCategory.Ocr, 4, Scan,
            "scanner", "escanear", "digitalização", "twain", "wia");

        // ----- Comparar -----
        T("compare", "Comparar PDFs", "Mostra diferenças de texto, páginas e imagens entre dois documentos, lado a lado.", ToolCategory.Compare, 6, Switch,
            "diferenças", "diff", "o que mudou", "comparação", "versões");

        // ----- Lote -----
        T("batch", "Fila de tarefas", "Processa centenas de documentos com operações encadeadas.", ToolCategory.Batch, 7, List,
            "processar vários arquivos", "em massa", "fluxo", "automação", "sequência de operações");
        T("watch-folder", "Monitorar pasta", "Executa uma operação automaticamente quando um PDF chega a uma pasta.", ToolCategory.Batch, 7, Folder,
            "pasta monitorada", "pasta de entrada", "automático", "hot folder");

        // ----- Vídeo -----
        T("video-compress", "Comprimir vídeo", "Converte vídeos de qualquer formato para MP4 em 360p, bem menores, para enviar e guardar.", ToolCategory.Video, 4, VideoGlyph,
            "reduzir vídeo", "diminuir vídeo", "converter vídeo", "mp4", "360p", "vídeo grande", "whatsapp", "compactar vídeo", "ffmpeg");

        return list;
    }
}
