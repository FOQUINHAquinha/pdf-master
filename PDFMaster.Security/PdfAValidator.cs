using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.Security;

/// <summary>
/// Verificação interna dos principais requisitos do PDF/A (partes 1, 2 e 3, nível B). Não substitui um
/// validador completo: cobre a estrutura do arquivo, metadados, cores, fontes, transparência, imagens,
/// ações, anotações, formulários, anexos e camadas. O resultado nunca é apresentado como conformidade.
/// </summary>
public static class PdfAValidator
{
    private sealed class Group(string id, string description)
    {
        public string Id { get; } = id;
        public string Description { get; } = description;
        public List<string> Problems { get; } = [];
        public bool NotVerified { get; set; }

        public void Add(string problem)
        {
            if (Problems.Count < 8 && !Problems.Contains(problem)) Problems.Add(problem);
            Count++;
        }

        public int Count { get; private set; }

        public PdfACheck ToCheck()
        {
            var problems = Problems.ToList();
            if (Count > Problems.Count) problems.Add($"… e mais {Count - Problems.Count} ocorrência(s).");
            return new PdfACheck(Id, Description, Count > 0 ? CheckStatus.Failed : NotVerified ? CheckStatus.NotVerified : CheckStatus.Passed, problems);
        }
    }

    private sealed class Groups
    {
        public readonly Group File = new("arquivo", "Cabeçalho, comentário binário, identificador (ID), fim do arquivo e ausência de criptografia");
        public readonly Group Structure = new("estrutura", "Estrutura e limites (sem fluxos de objetos no PDF/A-1, tamanhos de página, listas e textos)");
        public readonly Group Metadata = new("metadados", "Metadados XMP sem compressão, identificação PDF/A e coerência com as propriedades do documento");
        public readonly Group Color = new("cores", "Perfil de cor de saída (OutputIntent) e cores compatíveis com ele");
        public readonly Group Fonts = new("fontes", "Fontes incorporadas, com codificação, mapas e larguras coerentes");
        public readonly Group Transparency = new("transparencia", "Transparência (proibida no PDF/A-1) e modos de mesclagem permitidos");
        public readonly Group Images = new("imagens", "Imagens e fluxos sem interpolação, alternativas, LZW, dados externos (e JPEG 2000 no PDF/A-1)");
        public readonly Group Actions = new("acoes", "Sem JavaScript, ações proibidas ou ações automáticas");
        public readonly Group Annotations = new("anotacoes", "Comentários de tipos permitidos, imprimíveis e com aparência própria");
        public readonly Group Forms = new("formularios", "Formulários sem XFA e sem geração automática de aparência");
        public readonly Group Attachments = new("anexos", "Anexos permitidos para o nível (proibidos no PDF/A-1 e 2; identificados no PDF/A-3)");
        public readonly Group Layers = new("camadas", "Camadas (conteúdo opcional) permitidas para o nível");

        public IEnumerable<Group> All => [File, Structure, Metadata, Color, Fonts, Transparency, Images, Actions, Annotations, Forms, Attachments, Layers];
    }

    /// <summary>Valida um arquivo. Sem <paramref name="level"/>, usa o nível que o arquivo declara (ou PDF/A-2b).</summary>
    public static PdfAValidation Validate(string path, PdfALevel? level = null, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        var pdfium = PdfiumRenderer.Inspect(full);
        if (!pdfium.Opened)
            throw new PdfEngineException(pdfium.PasswordRequired
                ? "O documento é protegido por senha: PDF/A não permite criptografia."
                : $"\"{Path.GetFileName(full)}\" não pôde ser aberto.");

        PdfDocument document;
        try
        {
            document = PdfReader.Open(full, PdfDocumentOpenMode.Import);
        }
        catch (PdfReaderException ex)
        {
            throw new PdfEngineException($"\"{Path.GetFileName(full)}\" não pôde ser lido: {ex.Message}", ex);
        }

        using (document)
        {
            var xmp = PdfMetadata.XmpBytes(document);
            var (part, conformance) = xmp is null ? (0, string.Empty) : Xmp.ReadPdfAId(xmp);
            var chosen = level ?? (part is >= 1 and <= 3 ? (PdfALevel)part : PdfALevel.A2b);
            var checks = Check(document, File.ReadAllBytes(full), pdfium.SecurityRevision >= 0, chosen, ct);
            return new PdfAValidation(full, chosen, part, conformance, checks, null);
        }
    }

    /// <summary>
    /// Verifica um documento. Sem <paramref name="file"/> (antes de salvar), os requisitos do arquivo
    /// gravado e a coerência das propriedades ficam de fora.
    /// </summary>
    internal static IReadOnlyList<PdfACheck> Check(PdfDocument document, byte[]? file, bool encrypted, PdfALevel level, CancellationToken ct)
    {
        var g = new Groups();
        if (file is not null) CheckFile(g.File, g.Structure, file, encrypted, level);
        ct.ThrowIfCancellationRequested();
        var nodes = PdfGraph.Dictionaries(document);
        CheckMetadata(g.Metadata, document, file is not null, level);
        var components = CheckOutputIntent(g.Color, document, level);
        CheckLimits(g.Structure, document, nodes, level);

        var catalog = document.Internals.Catalog;
        var cmykDefault = nodes.Any(n => n.Dictionary.Elements.ContainsKey("/DefaultCMYK"));
        var rgbDefault = nodes.Any(n => n.Dictionary.Elements.ContainsKey("/DefaultRGB"));
        var spaces = new HashSet<string>();
        var fileSpecs = new List<PdfDictionary>();
        var associated = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var (d, key, parentKey) in nodes)
        {
            ct.ThrowIfCancellationRequested();
            var subtype = d.Elements.GetName("/Subtype");

            if (d.Stream is not null)
            {
                foreach (var filter in PdfA.Filters(d))
                {
                    if (filter == "/LZWDecode") g.Images.Add("Fluxo compactado com LZW (proibido).");
                    if (filter == "/JPXDecode" && level == PdfALevel.A1b) g.Images.Add("Imagem JPEG 2000 (não permitida no PDF/A-1).");
                }

                if (d.Elements.ContainsKey("/F") && d.Elements.GetName("/Type") != "/Filespec" && !d.Elements.ContainsKey("/Rect"))
                    g.Images.Add("Fluxo com dados em arquivo externo (chave /F).");
                if (d.Elements.ContainsKey("/FFilter") || d.Elements.ContainsKey("/FDecodeParms"))
                    g.Images.Add("Fluxo com filtros de arquivo externo.");
            }

            if (PdfA.IsImage(d))
            {
                if (d.Elements.GetBoolean("/Interpolate")) g.Images.Add("Imagem com interpolação ligada.");
                if (d.Elements.ContainsKey("/Alternates")) g.Images.Add("Imagem com versões alternativas (/Alternates).");
                if (d.Elements.ContainsKey("/OPI")) g.Images.Add("Imagem com referência OPI.");
                if ((d.Elements.ContainsKey("/SMask") || d.Elements.GetInteger("/SMaskInData") != 0) && level == PdfALevel.A1b)
                    g.Transparency.Add("Imagem com máscara de transparência (/SMask).");

                if (d.Elements.ContainsKey("/Intent") && !IsRenderingIntent(d.Elements.GetName("/Intent")))
                    g.Images.Add("Imagem com intenção de renderização inválida.");
                CollectSpace(PdfA.Resolve(d.Elements["/ColorSpace"]), spaces);
            }
            else if (PdfA.IsForm(d))
            {
                if (d.Elements.ContainsKey("/OPI")) g.Images.Add("Formulário (XObject) com referência OPI.");
                if (d.Elements.ContainsKey("/Ref")) g.Images.Add("Formulário que referencia outro arquivo (/Ref).");
                if (d.Elements.GetName("/Subtype2") == "/PS") g.Images.Add("Formulário PostScript.");
            }
            else if (subtype == "/PS" && d.Stream is not null)
            {
                g.Images.Add("Fragmento PostScript (proibido).");
            }

            if (d.Elements.GetDictionary("/Group") is { } group && group.Elements.GetName("/S") == "/Transparency" && level == PdfALevel.A1b)
                g.Transparency.Add(d.Elements.GetName("/Type") == "/Page" ? "Página com grupo de transparência." : "Grupo de transparência em formulário.");

            if (parentKey == "/ExtGState" || d.Elements.GetName("/Type") == "/ExtGState")
                CheckGraphicsState(g.Transparency, d, level);

            if (d.Elements.ContainsKey("/ShadingType")) CollectSpace(PdfA.Resolve(d.Elements["/ColorSpace"]), spaces);
            if (PdfA.IsFont(d)) CheckFont(g.Fonts, d, level);

            if (PdfA.IsActionKey(key, parentKey) && d.Elements.ContainsKey("/S"))
            {
                var type = d.Elements.GetName("/S");
                if (PdfA.ForbiddenAction(type, level)) g.Actions.Add($"Ação proibida: {type.TrimStart('/')}.");
                if (type == "/Named" && !PdfA.AllowedNamedActions.Contains(d.Elements.GetName("/N")))
                    g.Actions.Add($"Ação nomeada não permitida: {d.Elements.GetName("/N").TrimStart('/')}.");
            }

            if (d.Elements.ContainsKey("/JS")) g.Actions.Add("Código JavaScript.");

            if (PdfA.IsAnnotation(d, key)) CheckAnnotation(g.Annotations, g.Actions, g.Transparency, d, level);
            else if (d.Elements.ContainsKey("/FT") && d.Elements.ContainsKey("/AA")) g.Actions.Add("Campo de formulário com ações automáticas.");

            if (d.Elements.GetName("/Type") == "/Page")
            {
                if (d.Elements.ContainsKey("/AA")) g.Actions.Add("Página com ações automáticas.");
                CheckPageSize(g.Structure, d);
            }

            if (d.Elements.GetName("/Type") == "/Filespec" || d.Elements.ContainsKey("/EF")) fileSpecs.Add(d);
            if (d.Elements.GetArray("/AF") is { } af)
            {
                for (var i = 0; i < af.Elements.Count; i++)
                {
                    if (PdfA.Resolve(af.Elements[i]) is PdfDictionary spec) associated.Add(spec);
                }
            }

            if (d.Elements.GetName("/Type") == "/Metadata" && !ReferenceEquals(d, catalog.Elements.GetDictionary("/Metadata")) &&
                level == PdfALevel.A1b && PdfA.Filters(d).Any())
                g.Metadata.Add("Bloco de metadados (de página ou objeto) compactado.");
        }

        // Espaços de cor nomeados nos recursos.
        foreach (var (d, _, _) in nodes)
        {
            if (d.Elements.GetDictionary("/ColorSpace") is { } named && !PdfA.IsImage(d) && !d.Elements.ContainsKey("/ShadingType"))
            {
                foreach (var name in named.Elements.Keys)
                {
                    if (name is "/DefaultRGB" or "/DefaultCMYK" or "/DefaultGray") continue;
                    CollectSpace(PdfA.Resolve(named.Elements[name]), spaces);
                }
            }
        }

        // Catálogo, formulários, anexos e camadas.
        if (catalog.Elements.ContainsKey("/AA")) g.Actions.Add("Ações automáticas no documento (/AA).");
        if (catalog.Elements.GetDictionary("/Names") is { } names)
        {
            if (names.Elements.ContainsKey("/JavaScript")) g.Actions.Add("JavaScript no documento.");
            if (names.Elements.ContainsKey("/EmbeddedFiles") && level != PdfALevel.A3b)
                g.Attachments.Add($"Arquivos anexados (não permitidos no {PdfA.Name(level)}).");
        }

        if (level != PdfALevel.A3b)
        {
            foreach (var spec in fileSpecs.Where(s => s.Elements.ContainsKey("/EF")))
                g.Attachments.Add("Arquivo incorporado: " + PdfA.Describe(spec.Elements.GetString("/F")));
        }
        else
        {
            foreach (var spec in fileSpecs.Where(s => s.Elements.ContainsKey("/EF")))
            {
                var name = PdfA.Describe(spec.Elements.GetString("/UF") is { Length: > 0 } uf ? uf : spec.Elements.GetString("/F"));
                if (!spec.Elements.ContainsKey("/F") || !spec.Elements.ContainsKey("/UF")) g.Attachments.Add($"Anexo \"{name}\" sem os nomes /F e /UF.");
                if (!spec.Elements.ContainsKey("/AFRelationship")) g.Attachments.Add($"Anexo \"{name}\" sem a relação com o documento (/AFRelationship).");
                if (spec.Elements.GetDictionary("/EF")?.Elements.GetDictionary("/F") is not { } stream || !stream.Elements.ContainsKey("/Subtype"))
                    g.Attachments.Add($"Anexo \"{name}\" sem o tipo do arquivo (MIME).");
                if (!associated.Contains(spec)) g.Attachments.Add($"Anexo \"{name}\" não associado ao documento (/AF).");
            }
        }

        if (catalog.Elements.ContainsKey("/NeedsRendering")) g.Forms.Add("O documento pede renderização dinâmica (XFA).");
        if (catalog.Elements.GetDictionary("/AcroForm") is { } acro)
        {
            if (acro.Elements.GetBoolean("/NeedAppearances")) g.Forms.Add("Formulário pede ao leitor que gere as aparências (NeedAppearances).");
            if (acro.Elements.ContainsKey("/XFA")) g.Forms.Add("Formulário XFA.");
        }

        if (catalog.Elements.GetDictionary("/OCProperties") is { } oc)
        {
            if (level == PdfALevel.A1b)
            {
                g.Layers.Add("Camadas (conteúdo opcional) não são permitidas no PDF/A-1.");
            }
            else
            {
                var configs = new List<PdfDictionary>();
                if (oc.Elements.GetDictionary("/D") is { } d) configs.Add(d);
                if (oc.Elements.GetArray("/Configs") is { } list)
                {
                    for (var i = 0; i < list.Elements.Count; i++)
                    {
                        if (list.Elements.GetDictionary(i) is { } c) configs.Add(c);
                    }
                }

                foreach (var config in configs)
                {
                    if (config.Elements.GetString("/Name").Length == 0) g.Layers.Add("Configuração de camadas sem nome.");
                    if (config.Elements.ContainsKey("/AS")) g.Layers.Add("Configuração de camadas com troca automática (/AS).");
                }
            }
        }

        // Conteúdo das páginas e formulários: cores usadas pelos operadores.
        CheckContent(g, document, nodes, spaces, ct);

        if (components > 0)
        {
            if (spaces.Contains("CMYK") && components != 4 && !cmykDefault) g.Color.Add("Cores CMYK sem perfil correspondente (o perfil de saída não é CMYK).");
            if (spaces.Contains("RGB") && components != 3 && !rgbDefault) g.Color.Add("Cores RGB sem perfil correspondente (o perfil de saída não é RGB).");
            if (spaces.Contains("Gray") && components is not (1 or 3 or 4)) g.Color.Add("Cores em tons de cinza sem perfil correspondente.");
        }

        return g.All.Select(group => group.ToCheck()).ToList();
    }

    private static void CheckFile(Group file, Group structure, byte[] bytes, bool encrypted, PdfALevel level)
    {
        if (encrypted) file.Add("O arquivo é criptografado (protegido).");
        if (bytes.Length < 16 || Encoding.ASCII.GetString(bytes, 0, 7) != "%PDF-1." || !char.IsAsciiDigit((char)bytes[7]))
        {
            file.Add("O arquivo não começa com o cabeçalho \"%PDF-1.n\".");
        }
        else
        {
            var at = 8;
            while (at < bytes.Length && bytes[at] is (byte)'\r' or (byte)'\n') at++;
            var high = 0;
            if (at < bytes.Length && bytes[at] == (byte)'%')
            {
                for (var i = at + 1; i < Math.Min(bytes.Length, at + 64) && bytes[i] is not ((byte)'\r' or (byte)'\n'); i++)
                {
                    if (bytes[i] > 127) high++;
                }
            }

            if (high < 4) file.Add("Falta o comentário binário logo após o cabeçalho.");
        }

        var tailLength = Math.Min(bytes.Length, 8192);
        var tail = Encoding.Latin1.GetString(bytes, bytes.Length - tailLength, tailLength);
        var eof = tail.LastIndexOf("%%EOF", StringComparison.Ordinal);
        if (eof < 0) file.Add("Falta o marcador de fim (%%EOF).");
        else if (tail[(eof + 5)..].TrimEnd('\r', '\n').Length > 0 || tail.Length - eof - 5 > 2) file.Add("Há dados depois do marcador de fim (%%EOF).");
        var trailer = tail.LastIndexOf("trailer", StringComparison.Ordinal);
        if (trailer < 0 || !tail[trailer..].Contains("/ID", StringComparison.Ordinal)) file.Add("O identificador do arquivo (/ID) está ausente.");

        if (level == PdfALevel.A1b)
        {
            var text = Encoding.Latin1.GetString(bytes);
            if (text.Contains("/ObjStm", StringComparison.Ordinal)) structure.Add("Fluxos de objetos compactados (não permitidos no PDF/A-1).");
            if (text.Contains("/Type/XRef", StringComparison.Ordinal) || text.Contains("/Type /XRef", StringComparison.Ordinal))
                structure.Add("Tabela de referências compactada (não permitida no PDF/A-1).");
        }
    }

    private static void CheckMetadata(Group group, PdfDocument document, bool saved, PdfALevel level)
    {
        var catalog = document.Internals.Catalog;
        var metadata = catalog.Elements.GetDictionary("/Metadata");
        if (metadata?.Stream is null)
        {
            group.Add("O documento não tem bloco de metadados XMP.");
            return;
        }

        if (PdfA.Filters(metadata).Any()) group.Add("O bloco XMP está compactado (precisa estar sem filtro).");
        var xmp = PdfMetadata.XmpBytes(document);
        if (xmp is null)
        {
            group.Add("O bloco XMP não pôde ser lido.");
            return;
        }

        var (part, conformance) = Xmp.ReadPdfAId(xmp);
        if (part != (int)level) group.Add(part == 0 ? "O XMP não declara PDF/A (pdfaid:part)." : $"O XMP declara PDF/A-{part}, não {PdfA.Name(level)}.");
        if (conformance is not ("B" or "A" or "U") || level == PdfALevel.A1b && conformance == "U")
            group.Add($"Nível de conformidade declarado inválido: \"{conformance}\".");
        if (!saved) return;

        var x = Xmp.ReadInfo(xmp);
        var info = document.Info.Elements;
        void Same(string key, string label, string? xmpValue)
        {
            if (!info.ContainsKey(key)) return;
            var value = info.GetString(key);
            if (!string.Equals(value, xmpValue, StringComparison.Ordinal))
                group.Add($"\"{label}\" diferente entre as propriedades e o XMP.");
        }

        Same("/Title", "Título", x.Title);
        Same("/Author", "Autor", x.Author);
        Same("/Subject", "Assunto", x.Subject);
        Same("/Keywords", "Palavras-chave", x.Keywords);
        Same("/Creator", "Criador", x.Creator);
        Same("/Producer", "Produtor", x.Producer);
        void SameDate(string key, string label, DateTimeOffset? xmpValue)
        {
            if (!info.ContainsKey(key)) return;
            var value = DocumentInfoWriter.ParseDate(info.GetString(key));
            if (value is null || xmpValue is null || value.Value.UtcDateTime != xmpValue.Value.UtcDateTime)
                group.Add($"\"{label}\" diferente entre as propriedades e o XMP.");
        }

        SameDate("/CreationDate", "Data de criação", x.Created);
        SameDate("/ModDate", "Data de modificação", x.Modified);
    }

    /// <summary>Confere o perfil de saída; devolve o número de componentes de cor dele (0 se ausente).</summary>
    private static int CheckOutputIntent(Group group, PdfDocument document, PdfALevel level)
    {
        var intents = document.Internals.Catalog.Elements.GetArray("/OutputIntents");
        if (intents is null || intents.Elements.Count == 0)
        {
            group.Add("Falta o perfil de cor de saída (OutputIntent).");
            return 0;
        }

        var components = 0;
        PdfDictionary? profile = null;
        for (var i = 0; i < intents.Elements.Count; i++)
        {
            if (intents.Elements.GetDictionary(i) is not { } intent) continue;
            var destination = intent.Elements.GetDictionary("/DestOutputProfile");
            if (destination is not null && profile is not null && !ReferenceEquals(destination, profile) && level != PdfALevel.A1b)
                group.Add("Há perfis de saída diferentes no mesmo documento.");
            if (intent.Elements.GetName("/S") != "/GTS_PDFA1") continue;
            if (destination?.Stream is null)
            {
                group.Add("O perfil de saída PDF/A não inclui o perfil ICC.");
                continue;
            }

            profile ??= destination;
            byte[] icc;
            try
            {
                icc = destination.Stream.UnfilteredValue;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                group.Add("O perfil ICC não pôde ser lido.");
                continue;
            }

            if (icc.Length < 128)
            {
                group.Add("Perfil ICC inválido.");
                continue;
            }

            var space = Encoding.ASCII.GetString(icc, 16, 4);
            var deviceClass = Encoding.ASCII.GetString(icc, 12, 4);
            components = space switch { "RGB " => 3, "CMYK" => 4, "GRAY" => 1, _ => 0 };
            if (components == 0) group.Add($"Perfil ICC com espaço de cor não suportado ({space.Trim()}).");
            if (deviceClass is not ("mntr" or "prtr")) group.Add($"Perfil ICC de classe inadequada ({deviceClass}).");
            if (destination.Elements.ContainsKey("/N") && destination.Elements.GetInteger("/N") != components) group.Add("O número de componentes do perfil ICC (/N) não confere.");
            var major = icc[8];
            if (level == PdfALevel.A1b && major >= 4 || major >= 5) group.Add($"Versão do perfil ICC ({major}) não permitida neste nível.");
        }

        if (profile is null && group.Count == 0) group.Add("Nenhum perfil de saída do tipo PDF/A (GTS_PDFA1).");
        return components;
    }

    private static void CheckLimits(Group group, PdfDocument document, List<GraphNode> nodes, PdfALevel level)
    {
        if (document.Internals.AllObjects.Length > 8_388_607) group.Add("Objetos demais no arquivo.");
        foreach (var (d, _, _) in nodes)
        {
            if (level == PdfALevel.A1b && d.Elements.Count > 4095) group.Add("Dicionário com mais de 4095 entradas.");
            foreach (var key in d.Elements.Keys)
            {
                if (Encoding.UTF8.GetByteCount(key) > 128) group.Add("Nome com mais de 127 bytes.");
                switch (d.Elements[key])
                {
                    case PdfString s when s.Value.Length > 32767 && Encoding.Latin1.GetByteCount(s.Value) > 65535:
                        group.Add("Texto com mais de 65535 bytes.");
                        break;
                    case PdfReal r when level == PdfALevel.A1b && Math.Abs(r.Value) > 32767:
                        group.Add("Número real maior que 32767.");
                        break;
                }
            }
        }

        if (level == PdfALevel.A1b)
        {
            foreach (var array in PdfGraph.Arrays(nodes))
            {
                if (array.Elements.Count > 8191) group.Add("Lista com mais de 8191 itens.");
            }
        }
    }

    private static void CheckPageSize(Group group, PdfDictionary page)
    {
        if (page.Elements.GetArray("/MediaBox") is not { Elements.Count: 4 } box) return;
        var width = Math.Abs(box.Elements.GetReal(2) - box.Elements.GetReal(0));
        var height = Math.Abs(box.Elements.GetReal(3) - box.Elements.GetReal(1));
        if (width < 3 || height < 3 || width > 14400 || height > 14400) group.Add($"Página com tamanho fora dos limites ({width:0} × {height:0} pt).");
    }

    private static void CheckGraphicsState(Group group, PdfDictionary gs, PdfALevel level)
    {
        var mask = PdfA.Resolve(gs.Elements["/SMask"]);
        if (mask is not null && mask is not PdfName { Value: "/None" } && level == PdfALevel.A1b)
            group.Add("Máscara suave (/SMask) em estado gráfico.");

        foreach (var key in new[] { "/CA", "/ca" })
        {
            if (gs.Elements.ContainsKey(key) && Math.Abs(gs.Elements.GetReal(key) - 1) > 1e-6 && level == PdfALevel.A1b)
                group.Add("Opacidade menor que 100% (/CA ou /ca).");
        }

        if (gs.Elements.ContainsKey("/BM"))
        {
            var modes = new List<string>();
            switch (PdfA.Resolve(gs.Elements["/BM"]))
            {
                case PdfName n: modes.Add(n.Value); break;
                case PdfArray a:
                    for (var i = 0; i < a.Elements.Count; i++) modes.Add(a.Elements.GetName(i));
                    break;
            }

            foreach (var mode in modes)
            {
                if (mode is "/Normal" or "/Compatible") continue;
                if (level == PdfALevel.A1b) group.Add($"Modo de mesclagem {mode.TrimStart('/')} (não permitido no PDF/A-1).");
                else if (!PdfA.StandardBlendModes.Contains(mode)) group.Add($"Modo de mesclagem desconhecido: {mode.TrimStart('/')}.");
            }
        }

        if (gs.Elements.ContainsKey("/TR")) group.Add("Função de transferência (/TR) em estado gráfico.");
        if (gs.Elements.ContainsKey("/TR2") && gs.Elements.GetName("/TR2") != "/Default") group.Add("Função de transferência (/TR2) em estado gráfico.");
        if (gs.Elements.ContainsKey("/HTP")) group.Add("Retícula /HTP em estado gráfico.");
        if (gs.Elements.ContainsKey("/HTO") && level != PdfALevel.A1b) group.Add("Origem de retícula /HTO em estado gráfico.");
    }

    private static void CheckAnnotation(Group group, Group actions, Group transparency, PdfDictionary a, PdfALevel level)
    {
        var subtype = a.Elements.GetName("/Subtype");
        var label = subtype.TrimStart('/');
        if (PdfA.ForbiddenAnnotation(subtype, level)) group.Add($"Anotação do tipo {label} não permitida no {PdfA.Name(level)}.");
        var popup = subtype == "/Popup";
        if (!a.Elements.ContainsKey("/F"))
        {
            if (!popup || level == PdfALevel.A1b) group.Add($"Anotação {label} sem indicadores (/F) de impressão.");
        }
        else
        {
            var flags = a.Elements.GetInteger("/F");
            if ((flags & 4) == 0 || (flags & (1 | 2 | 32 | 256)) != 0) group.Add($"Anotação {label} oculta ou não imprimível.");
        }

        if (level == PdfALevel.A1b && a.Elements.ContainsKey("/CA") && Math.Abs(a.Elements.GetReal("/CA") - 1) > 1e-6)
            transparency.Add($"Anotação {label} com transparência (/CA).");
        if (subtype == "/Widget" && a.Elements.ContainsKey("/AA")) actions.Add("Campo de formulário com ações automáticas.");
        if (a.Elements.ContainsKey("/AA") && subtype != "/Widget" && level != PdfALevel.A1b) actions.Add($"Anotação {label} com ações automáticas.");

        var rect = a.Elements.GetArray("/Rect");
        var empty = rect is { Elements.Count: 4 } && (Math.Abs(rect.Elements.GetReal(2) - rect.Elements.GetReal(0)) < 1e-6 || Math.Abs(rect.Elements.GetReal(3) - rect.Elements.GetReal(1)) < 1e-6);
        if (popup || subtype == "/Link" || empty) return;
        var ap = a.Elements.GetDictionary("/AP");
        if (ap is null)
        {
            group.Add($"Anotação {label} sem aparência própria (/AP).");
            return;
        }

        if (level != PdfALevel.A1b && (ap.Elements.ContainsKey("/D") || ap.Elements.ContainsKey("/R")))
            group.Add($"Anotação {label} com aparências extras (/D ou /R).");
        if (!ap.Elements.ContainsKey("/N")) group.Add($"Anotação {label} sem a aparência normal (/N).");
    }

    private static void CheckFont(Group group, PdfDictionary font, PdfALevel level)
    {
        var subtype = font.Elements.GetName("/Subtype");
        if (subtype == "/Type3") return;
        var baseName = font.Elements.GetName("/BaseFont").TrimStart('/');
        var name = baseName.Length > 7 && baseName[6] == '+' ? baseName[7..] : baseName;
        var subset = baseName.Length > 7 && baseName[6] == '+';

        if (subtype == "/Type0")
        {
            var descendant = font.Elements.GetArray("/DescendantFonts")?.Elements.GetDictionary(0);
            if (descendant is null)
            {
                group.Add($"Fonte composta \"{name}\" sem fonte descendente.");
                return;
            }

            var cidDescriptor = descendant.Elements.GetDictionary("/FontDescriptor");
            if (!Embedded(cidDescriptor)) group.Add($"Fonte \"{name}\" não incorporada.");
            if (descendant.Elements.GetName("/Subtype") == "/CIDFontType2" && !descendant.Elements.ContainsKey("/CIDToGIDMap"))
                group.Add($"Fonte \"{name}\" sem mapa de CID para glifo (/CIDToGIDMap).");
            if (level == PdfALevel.A1b && subset && cidDescriptor is not null && !cidDescriptor.Elements.ContainsKey("/CIDSet"))
                group.Add($"Subconjunto da fonte \"{name}\" sem a lista de CIDs (/CIDSet).");
            var encoding = PdfA.Resolve(font.Elements["/Encoding"]);
            if (encoding is PdfName n && n.Value is not ("/Identity-H" or "/Identity-V") && !n.Value.Contains('-'))
                group.Add($"Fonte \"{name}\" com codificação CMap desconhecida.");
            return;
        }

        var descriptor = font.Elements.GetDictionary("/FontDescriptor");
        if (!Embedded(descriptor))
        {
            group.Add($"Fonte \"{name}\" não incorporada.");
            return;
        }

        if (!font.Elements.ContainsKey("/Widths")) group.Add($"Fonte \"{name}\" sem a tabela de larguras.");
        if (level == PdfALevel.A1b && subset && subtype is "/Type1" or "/MMType1" && !descriptor!.Elements.ContainsKey("/CharSet"))
            group.Add($"Subconjunto Type 1 da fonte \"{name}\" sem a lista de glifos (/CharSet).");

        if (subtype != "/TrueType") return;
        var flags = descriptor!.Elements.GetInteger("/Flags");
        var symbolic = (flags & 4) != 0;
        var encodingItem = PdfA.Resolve(font.Elements["/Encoding"]);
        if (symbolic)
        {
            if (encodingItem is not null && level == PdfALevel.A1b) group.Add($"Fonte simbólica \"{name}\" com codificação (/Encoding).");
            return;
        }

        var baseEncoding = encodingItem switch
        {
            PdfName en => en.Value,
            PdfDictionary ed => ed.Elements.GetName("/BaseEncoding"),
            _ => string.Empty,
        };
        if (baseEncoding is not ("/WinAnsiEncoding" or "/MacRomanEncoding"))
        {
            group.Add($"Fonte TrueType \"{name}\" sem codificação WinAnsi ou MacRoman.");
            return;
        }

        if (level == PdfALevel.A1b && encodingItem is PdfDictionary withDifferences && withDifferences.Elements.ContainsKey("/Differences"))
            group.Add($"Fonte TrueType \"{name}\" com tabela de diferenças na codificação.");

        // Larguras do dicionário × larguras do programa da fonte.
        if (descriptor.Elements.GetDictionary("/FontFile2")?.Stream is not { } stream) return;
        TrueTypeInfo? ttf;
        try
        {
            ttf = TrueTypeInfo.TryParse(stream.UnfilteredValue);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ttf = null;
        }

        if (ttf is null)
        {
            group.Add($"O programa da fonte \"{name}\" não pôde ser lido.");
            return;
        }

        var widths = font.Elements.GetArray("/Widths");
        var first = font.Elements.GetInteger("/FirstChar");
        if (widths is null) return;
        var codePage = Codepage(baseEncoding);
        var wrong = 0;
        try
        {
            for (var i = 0; i < widths.Elements.Count; i++)
            {
                var code = first + i;
                if (code is < 0 or > 255) continue;
                var unicode = codePage.GetString([(byte)code]);
                if (unicode.Length != 1 || unicode[0] == '�') continue;
                var glyph = ttf.Glyph(unicode[0]);
                if (glyph == 0) continue;
                if (Math.Abs(widths.Elements.GetReal(i) - ttf.Width(glyph)) > 1) wrong++;
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            group.Add($"O programa da fonte \"{name}\" está malformado.");
            return;
        }

        if (wrong > 0) group.Add($"Fonte \"{name}\": {wrong} largura(s) diferentes das do programa da fonte.");
    }

    internal static Encoding Codepage(string baseEncoding)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(baseEncoding == "/MacRomanEncoding" ? 10000 : 1252);
    }

    private static bool Embedded(PdfDictionary? descriptor) =>
        descriptor is not null && (descriptor.Elements.ContainsKey("/FontFile") || descriptor.Elements.ContainsKey("/FontFile2") || descriptor.Elements.ContainsKey("/FontFile3"));

    private static bool IsRenderingIntent(string intent) =>
        intent is "/AbsoluteColorimetric" or "/RelativeColorimetric" or "/Saturation" or "/Perceptual";

    /// <summary>Registra os espaços de cor dependentes do dispositivo usados por um espaço de cor.</summary>
    internal static void CollectSpace(PdfItem? item, HashSet<string> spaces, int depth = 0)
    {
        if (depth > 4) return;
        switch (item)
        {
            case PdfName name:
                switch (name.Value)
                {
                    case "/DeviceRGB" or "/RGB": spaces.Add("RGB"); break;
                    case "/DeviceCMYK" or "/CMYK": spaces.Add("CMYK"); break;
                    case "/DeviceGray" or "/G": spaces.Add("Gray"); break;
                }

                break;
            case PdfArray array when array.Elements.Count > 0:
                var family = array.Elements.GetName(0);
                switch (family)
                {
                    case "/Indexed" or "/I" when array.Elements.Count > 1:
                        CollectSpace(PdfA.Resolve(array.Elements[1]), spaces, depth + 1);
                        break;
                    case "/Separation" when array.Elements.Count > 2:
                        CollectSpace(PdfA.Resolve(array.Elements[2]), spaces, depth + 1);
                        break;
                    case "/DeviceN" when array.Elements.Count > 2:
                        CollectSpace(PdfA.Resolve(array.Elements[2]), spaces, depth + 1);
                        break;
                    case "/Pattern" when array.Elements.Count > 1:
                        CollectSpace(PdfA.Resolve(array.Elements[1]), spaces, depth + 1);
                        break;
                    case "/ICCBased" or "/CalRGB" or "/CalGray" or "/Lab":
                        break;
                }

                break;
        }
    }

    private static void CheckContent(Groups g, PdfDocument document, List<GraphNode> nodes, HashSet<string> spaces, CancellationToken ct)
    {
        var failures = 0;
        var streams = new List<(PdfDictionary Owner, byte[]? Data, PdfDictionary? Resources)>();
        for (var i = 0; i < document.PageCount; i++)
        {
            var page = document.Pages[i];
            streams.Add((page, null, page.Elements.GetDictionary("/Resources")));
        }

        foreach (var (d, _, _) in nodes)
        {
            if (PdfA.IsForm(d))
            {
                byte[]? data = null;
                try
                {
                    data = d.Stream!.UnfilteredValue;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    failures++;
                }

                if (data is not null) streams.Add((d, data, d.Elements.GetDictionary("/Resources")));
            }
        }

        foreach (var (owner, data, resources) in streams)
        {
            ct.ThrowIfCancellationRequested();
            CSequence content;
            try
            {
                content = data is null ? ContentReader.ReadContent((PdfPage)owner) : ContentReader.ReadContent(data);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                failures++;
                continue;
            }

            var named = resources?.Elements.GetDictionary("/ColorSpace");
            Scan(content, named, spaces, g.Images);
        }

        if (failures > 0)
        {
            g.Color.NotVerified = true;
            if (g.Color.Problems.Count == 0)
                g.Color.Problems.Add($"{failures} conteúdo(s) de página não puderam ser analisados; as cores usadas neles não foram conferidas.");
        }
    }

    private static void Scan(CSequence sequence, PdfDictionary? named, HashSet<string> spaces, Group images)
    {
        foreach (var item in sequence)
        {
            switch (item)
            {
                case CSequence inner:
                    Scan(inner, named, spaces, images);
                    break;
                case COperator op:
                    switch (op.Name)
                    {
                        case "k" or "K": spaces.Add("CMYK"); break;
                        case "rg" or "RG": spaces.Add("RGB"); break;
                        case "g" or "G": spaces.Add("Gray"); break;
                        case "cs" or "CS" when op.Operands.Count > 0 && op.Operands[0] is CName name:
                            if (name.Name is "/DeviceRGB" or "/DeviceCMYK" or "/DeviceGray") CollectSpace(new PdfName(name.Name), spaces);
                            else if (named?.Elements[name.Name] is { } resource) CollectSpace(PdfA.Resolve(resource), spaces);
                            break;
                        case "PS":
                            images.Add("Operador PostScript no conteúdo.");
                            break;
                    }

                    break;
            }
        }
    }
}
