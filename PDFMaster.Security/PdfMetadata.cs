using System.Reflection;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFMaster.PdfEngine;

namespace PDFMaster.Security;

/// <summary>Propriedades do documento (dicionário de informações) e o que mais o arquivo declara.</summary>
/// <param name="HasXmp">O documento tem bloco de metadados XMP.</param>
/// <param name="PdfAPart">Parte PDF/A declarada no XMP (0 = não declara).</param>
public sealed record DocumentProperties(
    string Title = "", string Author = "", string Subject = "", string Keywords = "", string Creator = "", string Producer = "",
    DateTime? Created = null, DateTime? Modified = null, bool HasXmp = false, int PdfAPart = 0, string PdfAConformance = "");

/// <summary>Leitura, edição e remoção de metadados.</summary>
public sealed class PdfMetadata
{
    public const string OperationName = "Metadados";

    private readonly OutputPlanner _planner;

    public PdfMetadata(OutputPlanner planner) => _planner = planner;

    public static DocumentProperties Read(string path)
    {
        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            return Read(document);
        }
        catch (PdfReaderException ex)
        {
            throw new PdfEngineException($"\"{Path.GetFileName(path)}\" não pôde ser lido (protegido por senha ou danificado).", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfEngineException($"Não foi possível abrir \"{Path.GetFileName(path)}\": {ex.Message}", ex);
        }
    }

    internal static DocumentProperties Read(PdfDocument document)
    {
        var info = document.Info.Elements;
        // Valores lidos do arquivo são textos; datas definidas pelo PDFsharp na memória são PdfDate.
        string S(string key) => PdfA.Resolve(info[key]) switch
        {
            PdfString s => s.Value,
            PdfName n => n.Value.TrimStart('/'),
            PdfDate date => DocumentInfoWriter.PdfDate(new DateTimeOffset(date.Value)),
            _ => string.Empty,
        };
        DateTime? D(string key) => DocumentInfoWriter.ParseDate(S(key)) is { } d ? d.LocalDateTime : null;
        var xmp = XmpBytes(document);
        var (part, conformance) = xmp is null ? (0, string.Empty) : Xmp.ReadPdfAId(xmp);
        return new DocumentProperties(S("/Title"), S("/Author"), S("/Subject"), S("/Keywords"), S("/Creator"), S("/Producer"),
            D("/CreationDate"), D("/ModDate"), xmp is not null, part, conformance);
    }

    /// <summary>Bytes do bloco XMP do catálogo (já sem compressão), ou null.</summary>
    internal static byte[]? XmpBytes(PdfDocument document)
    {
        if (document.Internals.Catalog.Elements.GetDictionary("/Metadata") is not { Stream: { } stream }) return null;
        try
        {
            return stream.UnfilteredValue;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Grava as propriedades exatamente como informadas (campos vazios são removidos) e um bloco XMP
    /// coerente com elas. Se o documento declarava PDF/A, a declaração é mantida.
    /// </summary>
    public Task<ProducedFile> WriteAsync(string source, DocumentProperties properties, OutputTarget target, CancellationToken ct = default, ISet<string>? reserved = null)
    {
        var values = InfoValues.From(properties);
        return Run(source, "_metadados", target, reserved, ct, values, document =>
        {
            var declared = XmpBytes(document) is { } old ? Xmp.ReadPdfAId(old).Part : 0;
            SetXmp(document, Xmp.Build(values, declared, Guid.NewGuid().ToString()));
        });
    }

    /// <summary>Remove todas as propriedades e o bloco XMP (do documento e das páginas).</summary>
    public Task<ProducedFile> RemoveAllAsync(string source, OutputTarget target, CancellationToken ct = default, ISet<string>? reserved = null) =>
        Run(source, "_sem-metadados", target, reserved, ct, new InfoValues(null, null, null, null, null, null, null, null), document =>
        {
            document.Internals.Catalog.Elements.Remove("/Metadata");
            foreach (var page in document.Pages) page.Elements.Remove("/Metadata");
        });

    private Task<ProducedFile> Run(string source, string suffix, OutputTarget target, ISet<string>? reserved, CancellationToken ct,
        InfoValues values, Action<PdfDocument> change)
    {
        var full = Path.GetFullPath(source);
        var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(full) + suffix + ".pdf", target, OperationName, [full], reserved);
        return Task.Run(() =>
        {
            using var document = PdfProtection.Open(full, null);
            DisableAutomaticXmp(document);
            change(document);
            ct.ThrowIfCancellationRequested();
            var pages = document.PageCount;
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp =>
            {
                var stage = temp + ".pdfsharp";
                try
                {
                    document.Save(stage);
                    ct.ThrowIfCancellationRequested();
                    DocumentInfoWriter.Apply(stage, values, temp);
                }
                finally
                {
                    DocumentInfoWriter.TryDelete(stage);
                }
            });
            return new ProducedFile(output, bytes, pages);
        }, ct);
    }

    /// <summary>Troca o bloco XMP do catálogo por <paramref name="xmp"/> (sem compressão, como exige o PDF/A).</summary>
    internal static void SetXmp(PdfDocument document, byte[] xmp)
    {
        var metadata = new PdfDictionary(document);
        metadata.Elements.SetName("/Type", "/Metadata");
        metadata.Elements.SetName("/Subtype", "/XML");
        metadata.CreateStream(xmp);
        document.Internals.AddObject(metadata);
        document.Internals.Catalog.Elements.SetReference("/Metadata", metadata);
    }

    /// <summary>Desliga a geração automática de XMP do PDFsharp, quando a versão dele a tiver.</summary>
    internal static void DisableAutomaticXmp(PdfDocument document)
    {
        var options = document.Options;
        var property = options.GetType().GetProperty("ManualXmpGeneration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property is { CanWrite: true } && property.PropertyType == typeof(bool)) property.SetValue(options, true);
    }
}
