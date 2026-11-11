using System.IO.Compression;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.Security;

/// <summary>
/// Conversão para PDF/A-1b, 2b ou 3b preservando a aparência: incorpora fontes (com substitutas do
/// Windows de métricas compatíveis para as fontes padrão), acrescenta o perfil de cor sRGB do Windows e
/// os metadados XMP, retira o que o nível proíbe (JavaScript, ações, anexos etc.) e relata cada mudança.
/// Quando algo não pode ser resolvido sem alterar a aparência (transparência no PDF/A-1, cores CMYK sem
/// perfil, fontes não incorporáveis), a conversão para e explica o motivo. O arquivo só é salvo depois
/// de aprovado na verificação interna e, se o veraPDF estiver instalado, também nele.
/// </summary>
public sealed class PdfAConverter
{
    public const string OperationName = "Converter para PDF/A";

    private readonly OutputPlanner _planner;

    public PdfAConverter(OutputPlanner planner) => _planner = planner;

    /// <summary>Perfil sRGB que acompanha o Windows.</summary>
    public static string? SrgbProfile()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color", "sRGB Color Space Profile.icm");
        return File.Exists(path) ? path : null;
    }

    /// <param name="externalValidation">Usar o veraPDF quando estiver instalado.</param>
    public Task<PdfAConversion> ConvertAsync(string source, PdfALevel level, OutputTarget target, bool externalValidation = true,
        CancellationToken ct = default, ISet<string>? reserved = null)
    {
        var full = Path.GetFullPath(source);
        var suffix = "_PDFA-" + PdfA.Flavour(level);
        var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(full) + suffix + ".pdf", target, OperationName, [full], reserved);
        return Task.Run(() =>
        {
            var inspection = PdfiumRenderer.Inspect(full);
            if (inspection.PasswordRequired || inspection.SecurityRevision >= 0)
                throw new PdfEngineException("O documento é protegido. Remova a proteção antes de converter: o PDF/A não permite criptografia.");
            if (!inspection.Opened) throw new PdfEngineException($"\"{Path.GetFileName(full)}\" não pôde ser aberto.");

            var srgb = SrgbProfile();
            using var document = PdfProtection.Open(full, null);
            PdfMetadata.DisableAutomaticXmp(document);
            var changes = new List<string>();
            var blockers = new List<string>();
            var fixer = new Fixer(document, level, changes, blockers);
            fixer.Run(ct);

            var original = PdfMetadata.Read(document);
            var now = InfoValues.Truncate(DateTimeOffset.Now);
            var version = typeof(PdfAConverter).Assembly.GetName().Version;
            var values = new InfoValues(
                Blank(original.Title), Blank(original.Author), Blank(original.Subject), Blank(original.Keywords), Blank(original.Creator),
                $"PDF Master {version?.Major ?? 0}.{version?.Minor ?? 0}.{version?.Build ?? 0}",
                InfoValues.TruncateLocal(original.Created) ?? now, now);
            SetOutputIntent(document, srgb, changes, blockers);
            PdfMetadata.SetXmp(document, Xmp.Build(values, (int)level, Guid.NewGuid().ToString()));
            document.Version = level == PdfALevel.A1b ? 14 : 17;

            // Verificação antes de salvar: o que sobrou não tem conserto automático.
            ct.ThrowIfCancellationRequested();
            var preflight = PdfAValidator.Check(document, null, false, level, ct);
            var problems = blockers.Concat(preflight.Where(c => c.Status == CheckStatus.Failed).SelectMany(c => c.Problems)).Distinct().ToList();
            if (problems.Count > 0)
            {
                throw new PdfAConversionException(
                    $"O documento não pode ser convertido para {PdfA.Name(level)} sem perder conteúdo ou mudar a aparência." +
                    (level == PdfALevel.A1b ? " O PDF/A-2b aceita mais recursos (como transparência e camadas)." : string.Empty),
                    problems);
            }

            var pages = document.PageCount;
            PdfAValidation? validation = null;
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp =>
            {
                var stage = temp + ".pdfsharp";
                try
                {
                    document.Save(stage);
                    ct.ThrowIfCancellationRequested();
                    DocumentInfoWriter.Apply(stage, values, temp, level == PdfALevel.A1b ? 14 : 17);
                }
                finally
                {
                    DocumentInfoWriter.TryDelete(stage);
                }

                ct.ThrowIfCancellationRequested();
                using (var saved = PdfReader.Open(temp, PdfDocumentOpenMode.Import))
                {
                    var checks = PdfAValidator.Check(saved, File.ReadAllBytes(temp), PdfiumRenderer.Inspect(temp).SecurityRevision >= 0, level, ct);
                    var xmp = PdfMetadata.XmpBytes(saved);
                    var (part, conformance) = xmp is null ? (0, string.Empty) : Xmp.ReadPdfAId(xmp);
                    validation = new PdfAValidation(output, level, part, conformance, checks, null);
                }

                if (!validation.InternalPassed)
                {
                    throw new PdfAConversionException("A verificação do arquivo convertido encontrou problemas; ele não foi salvo.",
                        validation.Checks.Where(c => c.Status != CheckStatus.Passed).SelectMany(c => c.Problems).ToList(), validation);
                }

                if (externalValidation && VeraPdf.Validate(temp, level, ct) is { } external)
                {
                    validation = validation with { External = external };
                    if (external.Compliant == false)
                        throw new PdfAConversionException("O veraPDF reprovou o arquivo convertido; ele não foi salvo.", external.FailedRules, validation);
                }
            });

            return new PdfAConversion(new ProducedFile(output, bytes, pages), validation!, changes);
        }, ct);
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Define o perfil de saída PDF/A: reaproveita um perfil ICC válido que o documento já declare
    /// (PDF/X, por exemplo); senão, usa o sRGB do Windows.
    /// </summary>
    private static void SetOutputIntent(PdfDocument document, string? srgb, List<string> changes, List<string> blockers)
    {
        var catalog = document.Internals.Catalog;
        PdfDictionary? profile = null;
        string? condition = null;
        if (catalog.Elements.GetArray("/OutputIntents") is { } existing)
        {
            for (var i = 0; i < existing.Elements.Count && profile is null; i++)
            {
                if (existing.Elements.GetDictionary(i) is not { } intent) continue;
                if (intent.Elements.GetDictionary("/DestOutputProfile") is not { Stream: not null } candidate) continue;
                try
                {
                    var icc = candidate.Stream.UnfilteredValue;
                    if (icc.Length >= 128 && System.Text.Encoding.ASCII.GetString(icc, 12, 4) is "mntr" or "prtr" &&
                        System.Text.Encoding.ASCII.GetString(icc, 16, 4) is "RGB " or "CMYK" or "GRAY")
                    {
                        profile = candidate;
                        condition = intent.Elements.GetString("/OutputConditionIdentifier");
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Perfil ilegível: ignorado.
                }
            }
        }

        if (profile is null)
        {
            if (srgb is null)
            {
                blockers.Add("O perfil de cor sRGB do Windows não foi encontrado (spool\\drivers\\color\\sRGB Color Space Profile.icm).");
                return;
            }

            var icc = File.ReadAllBytes(srgb);
            profile = new PdfDictionary(document);
            profile.Elements.SetInteger("/N", 3);
            profile.CreateStream(icc);
            document.Internals.AddObject(profile);
            condition = "sRGB IEC61966-2.1";
            changes.Add("Perfil de cor de saída sRGB (do Windows) acrescentado.");
        }
        else
        {
            changes.Add("O perfil de cor que o documento já declarava foi mantido como perfil de saída PDF/A.");
        }

        var output = new PdfDictionary(document);
        output.Elements.SetName("/Type", "/OutputIntent");
        output.Elements.SetName("/S", "/GTS_PDFA1");
        output.Elements.SetString("/OutputConditionIdentifier", string.IsNullOrEmpty(condition) ? "Custom" : condition);
        output.Elements.SetString("/Info", string.IsNullOrEmpty(condition) ? "Perfil de saída" : condition);
        if (condition == "sRGB IEC61966-2.1") output.Elements.SetString("/RegistryName", "http://www.color.org");
        if (profile.Reference is null) document.Internals.AddObject(profile);
        output.Elements.SetReference("/DestOutputProfile", profile);
        document.Internals.AddObject(output);
        var array = new PdfArray(document);
        array.Elements.Add(output.Reference!);
        catalog.Elements["/OutputIntents"] = array;
    }

    /// <summary>Correções aplicadas ao documento (cada uma relatada em <c>changes</c>).</summary>
    private sealed class Fixer(PdfDocument document, PdfALevel level, List<string> changes, List<string> blockers)
    {
        private readonly Dictionary<string, PdfDictionary> _fontFiles = new(StringComparer.OrdinalIgnoreCase);

        public void Run(CancellationToken ct)
        {
            var catalog = document.Internals.Catalog;
            RemoveSignatures(catalog);
            ct.ThrowIfCancellationRequested();

            var nodes = PdfGraph.Dictionaries(document);
            int automatic = 0, actions = 0, interpolation = 0, alternates = 0, transfer = 0, metadata = 0, charSets = 0;
            var transparency = false;
            foreach (var (d, key, parentKey) in nodes)
            {
                if (d.Elements.ContainsKey("/AA"))
                {
                    d.Elements.Remove("/AA");
                    automatic++;
                }

                foreach (var actionKey in new[] { "/A", "/OpenAction", "/Next" })
                {
                    if (d.Elements.ContainsKey(actionKey) && Forbidden(PdfA.Resolve(d.Elements[actionKey])))
                    {
                        d.Elements.Remove(actionKey);
                        actions++;
                    }
                }

                if (PdfA.IsImage(d))
                {
                    if (d.Elements.GetBoolean("/Interpolate"))
                    {
                        d.Elements.SetBoolean("/Interpolate", false);
                        interpolation++;
                    }

                    if (d.Elements.ContainsKey("/Alternates") || d.Elements.ContainsKey("/OPI"))
                    {
                        d.Elements.Remove("/Alternates");
                        d.Elements.Remove("/OPI");
                        alternates++;
                    }

                    if (d.Elements.ContainsKey("/SMask") || d.Elements.GetInteger("/SMaskInData") != 0) transparency = true;
                }

                if (PdfA.IsForm(d) && d.Elements.ContainsKey("/OPI"))
                {
                    d.Elements.Remove("/OPI");
                    alternates++;
                }

                if (parentKey == "/ExtGState" || d.Elements.GetName("/Type") == "/ExtGState")
                {
                    if (d.Elements.ContainsKey("/TR") || d.Elements.ContainsKey("/HTP") || level != PdfALevel.A1b && d.Elements.ContainsKey("/HTO"))
                    {
                        d.Elements.Remove("/TR");
                        d.Elements.Remove("/HTP");
                        if (level != PdfALevel.A1b) d.Elements.Remove("/HTO");
                        transfer++;
                    }

                    if (d.Elements.ContainsKey("/TR2") && d.Elements.GetName("/TR2") != "/Default")
                    {
                        d.Elements.SetName("/TR2", "/Default");
                        transfer++;
                    }

                    var mask = PdfA.Resolve(d.Elements["/SMask"]);
                    if (mask is not null && mask is not PdfName { Value: "/None" }) transparency = true;
                    if (d.Elements.ContainsKey("/CA") && Math.Abs(d.Elements.GetReal("/CA") - 1) > 1e-6 ||
                        d.Elements.ContainsKey("/ca") && Math.Abs(d.Elements.GetReal("/ca") - 1) > 1e-6)
                        transparency = true;
                    if (PdfA.Resolve(d.Elements["/BM"]) is PdfName { Value: not ("/Normal" or "/Compatible") } or PdfArray) transparency = true;
                }

                if (d.Elements.GetName("/Type") == "/Metadata" && d.Stream is not null && !ReferenceEquals(d, catalog.Elements.GetDictionary("/Metadata")))
                    metadata++;

                if (PdfA.IsFont(d)) FixFont(d);

                if (key == "/FontDescriptor" && level != PdfALevel.A1b)
                {
                    if (d.Elements.ContainsKey("/CIDSet") || d.Elements.ContainsKey("/CharSet"))
                    {
                        d.Elements.Remove("/CIDSet");
                        d.Elements.Remove("/CharSet");
                        charSets++;
                    }
                }
            }

            // Metadados de objetos (páginas, imagens, fontes) não acompanham a revisão do documento.
            if (metadata > 0)
            {
                foreach (var (d, _, _) in nodes)
                {
                    if (!ReferenceEquals(d, catalog) && d.Elements.GetDictionary("/Metadata") is { Stream: not null }) d.Elements.Remove("/Metadata");
                }

                changes.Add($"{metadata} bloco(s) de metadados de objetos (páginas, imagens ou fontes) removido(s).");
            }

            if (automatic > 0) changes.Add($"{automatic} conjunto(s) de ações automáticas (/AA) removido(s).");
            if (actions > 0) changes.Add($"{actions} ação(ões) proibida(s) removida(s) (JavaScript, abrir arquivos, sons etc.).");
            if (interpolation > 0) changes.Add($"Interpolação desligada em {interpolation} imagem(ns) (o PDF/A exige a imagem como está).");
            if (alternates > 0) changes.Add($"{alternates} referência(s) a versões alternativas ou externas de imagens removida(s).");
            if (transfer > 0) changes.Add($"{transfer} ajuste(s) de transferência/retícula de impressão removido(s) (proibidos no PDF/A).");
            if (charSets > 0) changes.Add($"{charSets} lista(s) opcional(is) de glifos de fontes removida(s) (seriam conferidas e podiam estar incompletas).");

            if (catalog.Elements.GetDictionary("/Names") is { } names && names.Elements.ContainsKey("/JavaScript"))
            {
                names.Elements.Remove("/JavaScript");
                changes.Add("Scripts (JavaScript) do documento removidos.");
            }

            FixTransparencyGroups(nodes, transparency);
            FixForms(catalog);
            FixAttachments(catalog, nodes);
            FixLayers(catalog);
            FixAnnotations(ct);
        }

        private bool Forbidden(PdfItem? action)
        {
            switch (action)
            {
                case PdfDictionary d when d.Elements.ContainsKey("/S"):
                    var type = d.Elements.GetName("/S");
                    if (PdfA.ForbiddenAction(type, level)) return true;
                    if (type == "/Named" && !PdfA.AllowedNamedActions.Contains(d.Elements.GetName("/N"))) return true;
                    return d.Elements.ContainsKey("/Next") && Forbidden(PdfA.Resolve(d.Elements["/Next"]));
                case PdfArray array:
                    for (var i = 0; i < array.Elements.Count; i++)
                    {
                        if (Forbidden(PdfA.Resolve(array.Elements[i]))) return true;
                    }

                    return false;
                default:
                    return false;
            }
        }

        /// <summary>Assinaturas digitais deixam de corresponder ao arquivo regravado: os valores saem, a aparência fica.</summary>
        private void RemoveSignatures(PdfDictionary catalog)
        {
            var removed = 0;
            foreach (var (d, _, _) in PdfGraph.Dictionaries(document))
            {
                if (d.Elements.GetName("/FT") == "/Sig" && d.Elements.ContainsKey("/V"))
                {
                    d.Elements.Remove("/V");
                    removed++;
                }
            }

            catalog.Elements.Remove("/Perms");
            catalog.Elements.GetDictionary("/AcroForm")?.Elements.Remove("/SigFlags");
            if (removed > 0)
                changes.Add($"{removed} assinatura(s) digital(is) retirada(s): o arquivo convertido não é o documento que foi assinado (a aparência da assinatura continua visível).");
        }

        private void FixTransparencyGroups(List<GraphNode> nodes, bool transparency)
        {
            if (level != PdfALevel.A1b) return;
            var groups = nodes.Where(n => n.Dictionary.Elements.GetDictionary("/Group") is { } g && g.Elements.GetName("/S") == "/Transparency").ToList();
            if (transparency)
            {
                blockers.Add("O documento usa transparência (opacidade, máscaras ou modos de mesclagem), que o PDF/A-1 não permite.");
                return;
            }

            foreach (var node in groups) node.Dictionary.Elements.Remove("/Group");
            if (groups.Count > 0) changes.Add($"{groups.Count} grupo(s) de transparência sem efeito removido(s) (o documento não usa transparência).");
        }

        private void FixForms(PdfDictionary catalog)
        {
            if (catalog.Elements.ContainsKey("/NeedsRendering")) catalog.Elements.Remove("/NeedsRendering");
            if (catalog.Elements.GetDictionary("/AcroForm") is not { } acro) return;
            if (acro.Elements.ContainsKey("/XFA"))
            {
                acro.Elements.Remove("/XFA");
                changes.Add("Parte dinâmica do formulário (XFA) removida; os campos comuns foram mantidos.");
            }

            if (acro.Elements.ContainsKey("/NeedAppearances"))
            {
                acro.Elements.Remove("/NeedAppearances");
                changes.Add("Pedido de geração automática de aparências dos campos (NeedAppearances) removido.");
            }
        }

        private void FixAttachments(PdfDictionary catalog, List<GraphNode> nodes)
        {
            var names = catalog.Elements.GetDictionary("/Names");
            if (level != PdfALevel.A3b)
            {
                if (names?.Elements.GetDictionary("/EmbeddedFiles") is not null)
                {
                    names.Elements.Remove("/EmbeddedFiles");
                    changes.Add($"Arquivos anexados ao documento removidos ({PdfA.Name(level)} não permite anexos; o PDF/A-3b permite).");
                }

                return;
            }

            // PDF/A-3: anexos permitidos, desde que identificados e associados ao documento.
            var specs = new List<PdfDictionary>();
            if (names?.Elements.GetDictionary("/EmbeddedFiles") is { } tree) CollectTree(tree, specs, 0);
            foreach (var (d, key, _) in nodes)
            {
                if (key == "/FS" && d.Elements.ContainsKey("/EF") && !specs.Contains(d)) specs.Add(d);
            }

            if (specs.Count == 0) return;
            var af = catalog.Elements.GetArray("/AF") ?? new PdfArray(document);
            foreach (var spec in specs)
            {
                var file = spec.Elements.GetString("/UF") is { Length: > 0 } uf ? uf : spec.Elements.GetString("/F");
                if (file.Length == 0) file = "anexo";
                if (!spec.Elements.ContainsKey("/F")) spec.Elements.SetString("/F", file);
                if (!spec.Elements.ContainsKey("/UF")) spec.Elements["/UF"] = new PdfString(file, PdfStringEncoding.Unicode);
                if (!spec.Elements.ContainsKey("/AFRelationship")) spec.Elements.SetName("/AFRelationship", "/Unspecified");
                if (spec.Elements.GetDictionary("/EF")?.Elements.GetDictionary("/F") is { } stream && !stream.Elements.ContainsKey("/Subtype"))
                    stream.Elements["/Subtype"] = new PdfName("/" + Mime(file));
                if (spec.Reference is null) continue;
                var listed = false;
                for (var i = 0; i < af.Elements.Count; i++) listed |= ReferenceEquals(PdfA.Resolve(af.Elements[i]), spec);
                if (!listed) af.Elements.Add(spec.Reference);
            }

            catalog.Elements["/AF"] = af;
            if (specs.Any(s => s.Reference is null))
                blockers.Add("Há anexos guardados de um jeito que não permite associá-los ao documento.");
            changes.Add($"{specs.Count} anexo(s) identificado(s) com tipo de arquivo e relação com o documento (exigência do PDF/A-3).");
        }

        private static void CollectTree(PdfDictionary node, List<PdfDictionary> specs, int depth)
        {
            if (depth > 32) return;
            if (node.Elements.GetArray("/Names") is { } pairs)
            {
                for (var i = 1; i < pairs.Elements.Count; i += 2)
                {
                    if (pairs.Elements.GetDictionary(i) is { } spec) specs.Add(spec);
                }
            }

            if (node.Elements.GetArray("/Kids") is { } kids)
            {
                for (var i = 0; i < kids.Elements.Count; i++)
                {
                    if (kids.Elements.GetDictionary(i) is { } kid) CollectTree(kid, specs, depth + 1);
                }
            }
        }

        private static string Mime(string file) => Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".xml" => "text/xml",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".json" => "application/json",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream",
        };

        private void FixLayers(PdfDictionary catalog)
        {
            if (catalog.Elements.GetDictionary("/OCProperties") is not { } oc) return;
            if (level == PdfALevel.A1b)
            {
                blockers.Add("O documento tem camadas (conteúdo opcional), que o PDF/A-1 não permite.");
                return;
            }

            var fixedConfigs = 0;
            var configs = new List<PdfDictionary>();
            if (oc.Elements.GetDictionary("/D") is { } d) configs.Add(d);
            if (oc.Elements.GetArray("/Configs") is { } list)
            {
                for (var i = 0; i < list.Elements.Count; i++)
                {
                    if (list.Elements.GetDictionary(i) is { } c) configs.Add(c);
                }
            }

            for (var i = 0; i < configs.Count; i++)
            {
                var changed = false;
                if (configs[i].Elements.GetString("/Name").Length == 0)
                {
                    configs[i].Elements.SetString("/Name", i == 0 ? "Padrão" : $"Configuração {i}");
                    changed = true;
                }

                if (configs[i].Elements.ContainsKey("/AS"))
                {
                    configs[i].Elements.Remove("/AS");
                    changed = true;
                }

                if (changed) fixedConfigs++;
            }

            if (fixedConfigs > 0) changes.Add($"{fixedConfigs} configuração(ões) de camadas ajustada(s) (nome obrigatório, sem troca automática).");
        }

        private void FixAnnotations(CancellationToken ct)
        {
            int removedHidden = 0, removedNoAppearance = 0, removedTypes = 0, flags = 0, extraAppearances = 0;
            var removedNames = new List<string>();
            foreach (var page in document.Pages)
            {
                ct.ThrowIfCancellationRequested();
                if (page.Elements.GetArray("/Annots") is not { } annots) continue;
                var remove = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                for (var i = 0; i < annots.Elements.Count; i++)
                {
                    if (annots.Elements.GetDictionary(i) is not { } a) continue;
                    var subtype = a.Elements.GetName("/Subtype");
                    var label = subtype.TrimStart('/');
                    var f = a.Elements.GetInteger("/F");
                    var hasAppearance = a.Elements.GetDictionary("/AP")?.Elements.ContainsKey("/N") == true;
                    var rect = a.Elements.GetArray("/Rect");
                    var empty = rect is not { Elements.Count: 4 } ||
                                Math.Abs(rect.Elements.GetReal(2) - rect.Elements.GetReal(0)) < 1e-6 ||
                                Math.Abs(rect.Elements.GetReal(3) - rect.Elements.GetReal(1)) < 1e-6;

                    if (PdfA.ForbiddenAnnotation(subtype, level))
                    {
                        if (subtype is "/Watermark" or "/Redact" or "/Caret" && hasAppearance && (f & 2) == 0)
                        {
                            blockers.Add($"Anotação do tipo {label} (visível) não existe no PDF 1.4, base do PDF/A-1.");
                            continue;
                        }

                        remove.Add(a);
                        removedTypes++;
                        if (removedNames.Count < 5) removedNames.Add(label);
                        continue;
                    }

                    if ((f & (1 | 2)) != 0 && subtype != "/Widget")
                    {
                        // Oculta: não aparecia; sai do arquivo.
                        remove.Add(a);
                        removedHidden++;
                        continue;
                    }

                    if (subtype is not ("/Popup" or "/Link") && !empty && !hasAppearance)
                    {
                        if (subtype == "/Widget")
                        {
                            blockers.Add("Há campos de formulário sem aparência gerada. Preencha ou salve o formulário em \"Preencher formulários\" antes de converter.");
                            continue;
                        }

                        remove.Add(a);
                        removedNoAppearance++;
                        continue;
                    }

                    if (level == PdfALevel.A1b && a.Elements.ContainsKey("/CA") && Math.Abs(a.Elements.GetReal("/CA") - 1) > 1e-6)
                        blockers.Add($"Anotação {label} com transparência (não permitida no PDF/A-1).");

                    var wanted = (f | 4) & ~(1 | 2 | 32 | 256);
                    if (subtype == "/Widget" && (f & 2) != 0)
                        blockers.Add("Há campos de formulário ocultos; o PDF/A exige que todas as anotações sejam imprimíveis.");
                    else if (wanted != f || !a.Elements.ContainsKey("/F"))
                    {
                        a.Elements.SetInteger("/F", wanted);
                        flags++;
                    }

                    if (level != PdfALevel.A1b && a.Elements.GetDictionary("/AP") is { } ap && (ap.Elements.ContainsKey("/D") || ap.Elements.ContainsKey("/R")))
                    {
                        ap.Elements.Remove("/D");
                        ap.Elements.Remove("/R");
                        extraAppearances++;
                    }
                }

                if (remove.Count == 0) continue;
                // Os balões (Popup) das anotações removidas saem junto.
                for (var i = 0; i < annots.Elements.Count; i++)
                {
                    if (annots.Elements.GetDictionary(i) is { } a && a.Elements.GetName("/Subtype") == "/Popup" &&
                        a.Elements.GetDictionary("/Parent") is { } parent && remove.Contains(parent))
                        remove.Add(a);
                }

                for (var i = annots.Elements.Count - 1; i >= 0; i--)
                {
                    if (annots.Elements.GetDictionary(i) is { } a && remove.Contains(a)) annots.Elements.RemoveAt(i);
                }
            }

            if (removedTypes > 0) changes.Add($"{removedTypes} anotação(ões) de tipo não permitido removida(s) ({string.Join(", ", removedNames.Distinct())}).");
            if (removedHidden > 0) changes.Add($"{removedHidden} anotação(ões) oculta(s) removida(s).");
            if (removedNoAppearance > 0) changes.Add($"{removedNoAppearance} comentário(s) sem aparência própria removido(s) (o PDF/A exige que cada comentário tenha a sua).");
            if (flags > 0) changes.Add($"{flags} anotação(ões) marcada(s) como imprimível(is).");
            if (extraAppearances > 0) changes.Add($"Aparências de \"mouse por cima\"/\"clique\" retiradas de {extraAppearances} anotação(ões).");
        }

        private void FixFont(PdfDictionary font)
        {
            var subtype = font.Elements.GetName("/Subtype");
            if (subtype == "/Type3") return;
            var baseFont = font.Elements.GetName("/BaseFont").TrimStart('/');
            var subset = baseFont.Length > 7 && baseFont[6] == '+';
            var name = subset ? baseFont[7..] : baseFont;

            if (subtype == "/Type0")
            {
                if (font.Elements.GetArray("/DescendantFonts")?.Elements.GetDictionary(0) is not { } cid) return;
                if (cid.Elements.GetName("/Subtype") == "/CIDFontType2" && !cid.Elements.ContainsKey("/CIDToGIDMap"))
                    cid.Elements.SetName("/CIDToGIDMap", "/Identity");
                if (level == PdfALevel.A1b && subset && cid.Elements.GetDictionary("/FontDescriptor") is { } descriptor && !descriptor.Elements.ContainsKey("/CIDSet"))
                {
                    if (!AddCidSet(cid, descriptor)) blockers.Add($"Não foi possível montar a lista de glifos (/CIDSet) da fonte \"{name}\" exigida pelo PDF/A-1.");
                }

                return;
            }

            var existing = font.Elements.GetDictionary("/FontDescriptor");
            if (existing is not null && (existing.Elements.ContainsKey("/FontFile") || existing.Elements.ContainsKey("/FontFile2") || existing.Elements.ContainsKey("/FontFile3")))
                return;
            Embed(font, name, existing);
        }

        /// <summary>Incorpora uma fonte simples não incorporada usando o arquivo do Windows equivalente.</summary>
        private void Embed(PdfDictionary font, string name, PdfDictionary? existing)
        {
            var encodingItem = PdfA.Resolve(font.Elements["/Encoding"]);
            var encoding = encodingItem switch
            {
                PdfName n => n.Value,
                PdfDictionary e when !e.Elements.ContainsKey("/Differences") => e.Elements.GetName("/BaseEncoding"),
                _ => string.Empty,
            };
            if (encoding is not ("/WinAnsiEncoding" or "/MacRomanEncoding"))
            {
                blockers.Add($"A fonte \"{name}\" não está incorporada e usa uma codificação própria; não há como incorporá-la sem risco de trocar caracteres.");
                return;
            }

            var flags = existing?.Elements.GetInteger("/Flags") ?? 0;
            var substitute = FontSubstitutes.Find(name, (flags & 0x40000) != 0, (flags & 64) != 0);
            if (substitute is null)
            {
                blockers.Add($"A fonte \"{name}\" não está incorporada e não há fonte equivalente instalada no Windows.");
                return;
            }

            var (path, family, style, compatible) = substitute.Value;
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                blockers.Add($"O arquivo da fonte \"{family}\" não pôde ser lido: {ex.Message}");
                return;
            }

            var ttf = TrueTypeInfo.TryParse(data);
            if (ttf is null || !ttf.HasUnicodeCmap)
            {
                blockers.Add($"O arquivo da fonte \"{family}\" não pôde ser usado.");
                return;
            }

            if (ttf.EmbeddingRestricted)
            {
                blockers.Add($"A licença da fonte \"{family}\" não permite incorporá-la em documentos.");
                return;
            }

            var postScript = ttf.PostScriptName ?? family.Replace(" ", string.Empty, StringComparison.Ordinal);
            var codePage = PdfAValidator.Codepage(encoding);
            const int first = 32, last = 255;
            var widths = new PdfArray(document);
            try
            {
                for (var code = first; code <= last; code++)
                {
                    var text = codePage.GetString([(byte)code]);
                    var glyph = text.Length == 1 ? ttf.Glyph(text[0]) : 0;
                    widths.Elements.Add(new PdfReal(Math.Round(ttf.Width(glyph), 3)));
                }
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                blockers.Add($"O arquivo da fonte \"{family}\" está malformado.");
                return;
            }

            if (!_fontFiles.TryGetValue(path, out var fontFile))
            {
                fontFile = new PdfDictionary(document);
                fontFile.Elements.SetInteger("/Length1", data.Length);
                fontFile.Elements.SetName("/Filter", "/FlateDecode");
                fontFile.CreateStream(Deflate(data));
                document.Internals.AddObject(fontFile);
                _fontFiles[path] = fontFile;
            }

            var descriptor = new PdfDictionary(document);
            descriptor.Elements.SetName("/Type", "/FontDescriptor");
            descriptor.Elements.SetName("/FontName", "/" + postScript);
            descriptor.Elements.SetInteger("/Flags", 32 | (ttf.ItalicAngle != 0 || style.Contains("Itálico", StringComparison.Ordinal) ? 64 : 0));
            var box = new PdfArray(document);
            foreach (var v in ttf.BoundingBox) box.Elements.Add(new PdfReal(Math.Round(v, 3)));
            descriptor.Elements["/FontBBox"] = box;
            descriptor.Elements.SetReal("/ItalicAngle", Math.Round(ttf.ItalicAngle, 3));
            descriptor.Elements.SetReal("/Ascent", Math.Round(ttf.Ascent, 3));
            descriptor.Elements.SetReal("/Descent", Math.Round(ttf.Descent, 3));
            descriptor.Elements.SetReal("/CapHeight", Math.Round(ttf.CapHeight, 3));
            descriptor.Elements.SetInteger("/StemV", style.Contains("Negrito", StringComparison.Ordinal) ? 140 : 80);
            descriptor.Elements.SetReference("/FontFile2", fontFile);
            document.Internals.AddObject(descriptor);

            font.Elements.SetName("/Subtype", "/TrueType");
            font.Elements.SetName("/BaseFont", "/" + postScript);
            font.Elements.SetInteger("/FirstChar", first);
            font.Elements.SetInteger("/LastChar", last);
            font.Elements["/Widths"] = widths;
            font.Elements.SetName("/Encoding", encoding);
            font.Elements.SetReference("/FontDescriptor", descriptor);
            var note = compatible ? " (mesmas medidas da original)" : string.Empty;
            var change = $"Fonte \"{name}\" incorporada usando \"{family}\" {style} do Windows{note}.";
            if (!changes.Contains(change)) changes.Add(change);
        }

        /// <summary>Lista dos CIDs presentes no subconjunto (exigida pelo PDF/A-1).</summary>
        private bool AddCidSet(PdfDictionary cid, PdfDictionary descriptor)
        {
            if (descriptor.Elements.GetDictionary("/FontFile2")?.Stream is not { } stream) return false;
            TrueTypeInfo? ttf;
            byte[]? map = null;
            try
            {
                ttf = TrueTypeInfo.TryParse(stream.UnfilteredValue);
                if (cid.Elements.GetDictionary("/CIDToGIDMap")?.Stream is { } mapStream) map = mapStream.UnfilteredValue;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return false;
            }

            if (ttf is null) return false;
            bool[] present;
            try
            {
                present = ttf.PresentGlyphs();
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                return false;
            }

            var count = map is null ? present.Length : map.Length / 2;
            var bits = new byte[(count + 7) / 8];
            for (var c = 0; c < count; c++)
            {
                var glyph = map is null ? c : map[2 * c] << 8 | map[2 * c + 1];
                if (glyph < present.Length && present[glyph] && (glyph != 0 || c == 0)) bits[c / 8] |= (byte)(0x80 >> (c % 8));
            }

            var set = new PdfDictionary(document);
            set.Elements.SetName("/Filter", "/FlateDecode");
            set.CreateStream(Deflate(bits));
            document.Internals.AddObject(set);
            descriptor.Elements.SetReference("/CIDSet", set);
            return true;
        }

        private static byte[] Deflate(byte[] data)
        {
            using var memory = new MemoryStream();
            using (var zlib = new ZLibStream(memory, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
            return memory.ToArray();
        }
    }
}

/// <summary>Fontes do Windows usadas no lugar de fontes não incorporadas.</summary>
internal static class FontSubstitutes
{
    // Arquivos: normal, negrito, itálico, negrito itálico.
    private static readonly Dictionary<string, (string Family, string[] Files, bool MetricCompatible)> Families = new(StringComparer.Ordinal)
    {
        ["helvetica"] = ("Arial", ["arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"], true),
        ["arial"] = ("Arial", ["arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"], true),
        ["times"] = ("Times New Roman", ["times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf"], true),
        ["timesroman"] = ("Times New Roman", ["times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf"], true),
        ["timesnewroman"] = ("Times New Roman", ["times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf"], true),
        ["courier"] = ("Courier New", ["cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf"], true),
        ["couriernew"] = ("Courier New", ["cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf"], true),
        ["calibri"] = ("Calibri", ["calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf"], true),
        ["cambria"] = ("Cambria", ["cambria.ttc", "cambriab.ttf", "cambriai.ttf", "cambriaz.ttf"], true),
        ["consolas"] = ("Consolas", ["consola.ttf", "consolab.ttf", "consolai.ttf", "consolaz.ttf"], true),
        ["georgia"] = ("Georgia", ["georgia.ttf", "georgiab.ttf", "georgiai.ttf", "georgiaz.ttf"], true),
        ["segoeui"] = ("Segoe UI", ["segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf"], true),
        ["tahoma"] = ("Tahoma", ["tahoma.ttf", "tahomabd.ttf", "tahoma.ttf", "tahomabd.ttf"], true),
        ["trebuchetms"] = ("Trebuchet MS", ["trebuc.ttf", "trebucbd.ttf", "trebucit.ttf", "trebucbi.ttf"], true),
        ["verdana"] = ("Verdana", ["verdana.ttf", "verdanab.ttf", "verdanai.ttf", "verdanaz.ttf"], true),
    };

    /// <summary>
    /// Arquivo, família, estilo e se as medidas são as mesmas da original (fontes padrão do PDF e as
    /// próprias fontes do Windows). Null quando não há equivalente (incluindo fontes de símbolos).
    /// </summary>
    public static (string Path, string Family, string Style, bool MetricCompatible)? Find(string baseName, bool forceBold, bool italicFlag)
    {
        var lower = baseName.ToLowerInvariant();
        var bold = forceBold || lower.Contains("bold", StringComparison.Ordinal) || lower.Contains("black", StringComparison.Ordinal) ||
                   lower.Contains("heavy", StringComparison.Ordinal);
        var italic = italicFlag || lower.Contains("italic", StringComparison.Ordinal) || lower.Contains("oblique", StringComparison.Ordinal);
        var familyPart = lower.Split('-', ',')[0].Replace(" ", string.Empty, StringComparison.Ordinal);
        foreach (var suffix in new[] { "psmt", "mt", "ps" })
        {
            if (familyPart.EndsWith(suffix, StringComparison.Ordinal) && familyPart.Length > suffix.Length + 2)
            {
                familyPart = familyPart[..^suffix.Length];
                break;
            }
        }

        foreach (var style in new[] { "bolditalic", "boldoblique", "bold", "italic", "oblique" })
        {
            if (familyPart.EndsWith(style, StringComparison.Ordinal) && familyPart.Length > style.Length + 2)
            {
                familyPart = familyPart[..^style.Length];
                break;
            }
        }

        if (!Families.TryGetValue(familyPart, out var entry)) return null;
        var index = (bold ? 1 : 0) + (italic ? 2 : 0);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), entry.Files[index]);
        if (!File.Exists(path) || path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)) return null;
        var styleName = (bold, italic) switch
        {
            (true, true) => "Negrito Itálico",
            (true, false) => "Negrito",
            (false, true) => "Itálico",
            _ => "Normal",
        };
        return (path, entry.Family, styleName, entry.MetricCompatible);
    }
}
