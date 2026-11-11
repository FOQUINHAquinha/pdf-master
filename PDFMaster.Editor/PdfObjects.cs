using System.Reflection;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PDFMaster.PdfEngine;

namespace PDFMaster.Editor;

/// <summary>Operações de baixo nível sobre o PDF: XObjects, recursos, conteúdo e anotações.</summary>
internal static class PdfObjects
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Dicionário do Form XObject de um <see cref="XForm"/> desenhado pelo PDFsharp. O PDFsharp não
    /// expõe esse objeto publicamente; ele é obtido por reflexão (versão fixa do pacote) para que o
    /// formulário possa ser posicionado com uma matriz própria — o que funciona em páginas giradas.
    /// </summary>
    public static PdfDictionary FormOf(XForm form, PdfDocument document)
    {
        // Garante que o conteúdo foi gravado (normalmente já feito ao descartar o XGraphics).
        for (var type = form.GetType(); type is not null; type = type.BaseType)
        {
            var finish = type.GetMethod("Finish", Members | BindingFlags.DeclaredOnly, Type.EmptyTypes);
            if (finish is null) continue;
            finish.Invoke(form, null);
            break;
        }

        object? value = null;
        for (var type = form.GetType(); type is not null && value is null; type = type.BaseType)
        {
            value = type.GetProperty("PdfForm", Members)?.GetValue(form)
                    ?? type.GetField("_pdfForm", Members)?.GetValue(form)
                    ?? type.GetField("pdfForm", Members)?.GetValue(form);
        }

        if (value is not PdfDictionary dictionary)
            throw new InvalidOperationException("Não foi possível obter o conteúdo desenhado (XForm) do PDFsharp.");
        if (dictionary.Reference is null) document.Internals.AddObject(dictionary);
        return dictionary;
    }

    /// <summary>Desenha com o PDFsharp num XObject do tamanho dado (coordenadas: origem em cima à esquerda, y para baixo).</summary>
    public static PdfDictionary Draw(PdfDocument document, double width, double height, Action<XGraphics> draw)
    {
        WindowsFonts.EnsureInstalled();
        var form = new XForm(document, new XSize(Math.Max(1, width), Math.Max(1, height)));
        using (var gfx = XGraphics.FromForm(form))
        {
            draw(gfx);
        }

        return FormOf(form, document);
    }

    /// <summary>XObject escrito à mão (operadores do PDF), com recursos opcionais.</summary>
    public static PdfDictionary ManualForm(PdfDocument document, double width, double height, string content, PdfDictionary? resources = null)
    {
        var form = new PdfDictionary(document);
        form.Elements["/Type"] = new PdfName("/XObject");
        form.Elements["/Subtype"] = new PdfName("/Form");
        form.Elements["/BBox"] = Numbers(document, 0, 0, Math.Max(1, width), Math.Max(1, height));
        form.Elements["/Resources"] = resources ?? new PdfDictionary(document);
        document.Internals.AddObject(form);
        form.CreateStream(Encoding.ASCII.GetBytes(content));
        return form;
    }

    public static PdfArray Numbers(PdfDocument document, params double[] values) =>
        new(document, values.Select(v => (PdfItem)new PdfReal(Math.Round(v, 4))).ToArray());

    public static PdfArray Matrix(PdfDocument document, Affine m) => Numbers(document, m.A, m.B, m.C, m.D, m.E, m.F);

    public static PdfDictionary Resources(PdfDocument document, PdfPage page)
    {
        var resources = page.Elements.GetDictionary("/Resources");
        if (resources is null)
        {
            resources = new PdfDictionary(document);
            page.Elements["/Resources"] = resources;
        }

        return resources;
    }

    public static PdfDictionary SubDictionary(PdfDocument document, PdfDictionary parent, string key)
    {
        var child = parent.Elements.GetDictionary(key);
        if (child is null)
        {
            child = new PdfDictionary(document);
            parent.Elements[key] = child;
        }

        return child;
    }

    /// <summary>
    /// Coloca XObjects na página com as matrizes dadas: "por baixo" (antes do conteúdo existente) ou
    /// "por cima" (depois dele, com o conteúdo existente isolado em q/Q).
    /// </summary>
    public static void Place(PdfDocument document, PdfPage page, IReadOnlyList<(PdfDictionary Form, Affine Matrix, bool Behind)> placements)
    {
        if (placements.Count == 0) return;
        var xobjects = SubDictionary(document, Resources(document, page), "/XObject");
        var under = new StringBuilder();
        var over = new StringBuilder();
        var counter = 0;
        foreach (var (form, matrix, behind) in placements)
        {
            string name;
            do
            {
                name = $"/PMx{++counter}";
            }
            while (xobjects.Elements.ContainsKey(name));

            xobjects.Elements[name] = form.Reference!;
            (behind ? under : over).Append("q ").Append(matrix.ToPdfOperator()).Append(' ').Append(name).Append(" Do Q\n");
        }

        var contents = page.Contents;
        if (over.Length > 0)
        {
            contents.PrependContent().CreateStream("q\n"u8.ToArray());
            contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("\nQ\n" + over));
        }

        if (under.Length > 0) contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes(under.ToString()));
    }

    /// <summary>Cria uma anotação, já ligada à página.</summary>
    public static PdfDictionary Annotation(PdfDocument document, PdfPage page, string subtype, double[] rect)
    {
        var annotation = new PdfDictionary(document);
        annotation.Elements["/Type"] = new PdfName("/Annot");
        annotation.Elements["/Subtype"] = new PdfName(subtype);
        annotation.Elements["/Rect"] = Numbers(document, rect);
        annotation.Elements["/F"] = new PdfInteger(4); // imprimir
        annotation.Elements["/P"] = page.Reference!;
        annotation.Elements["/M"] = new PdfString(Pdf.Date(DateTimeOffset.Now));
        annotation.Elements["/NM"] = new PdfString("pdfmaster-" + Guid.NewGuid().ToString("N"));
        document.Internals.AddObject(annotation);

        var annots = page.Elements.GetArray("/Annots");
        if (annots is null)
        {
            annots = new PdfArray(document);
            page.Elements["/Annots"] = annots;
        }

        annots.Elements.Add(annotation.Reference!);
        return annotation;
    }

    public static void SetAppearance(PdfDictionary annotation, PdfDictionary normal)
    {
        var appearance = new PdfDictionary(annotation.Owner);
        appearance.Elements["/N"] = normal.Reference!;
        annotation.Elements["/AP"] = appearance;
    }

    public static PdfString Text(string value) => new(value, PdfStringEncoding.Unicode);

    public static PdfArray Color(PdfDocument document, Rgb color) => Numbers(document, color.R / 255.0, color.G / 255.0, color.B / 255.0);
}
