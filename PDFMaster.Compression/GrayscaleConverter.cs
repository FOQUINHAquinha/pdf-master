using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PDFMaster.Core.Files;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.Compression;

/// <summary>Resultado da conversão: o arquivo, o que foi convertido e as páginas em que ainda sobrou cor.</summary>
public sealed record GrayscaleResult(ProducedFile File, int ColorOperators, int Images, int Shadings, IReadOnlyList<int> PagesStillInColor,
    IReadOnlyList<string> Notes);

/// <summary>
/// Converte o PDF para tons de cinza sem transformar as páginas em imagem: o texto continua texto
/// (selecionável e pesquisável). As cores dos desenhos e textos (RGB e CMYK) viram cinza pela
/// luminância, as imagens RGB, CMYK e indexadas são regravadas em cinza e os degradês simples
/// também. No fim, cada página é renderizada e conferida; as que ainda mostram cor são informadas.
/// O original nunca é alterado.
/// </summary>
public sealed class GrayscaleConverter
{
    public const string OperationName = "Converter para preto e branco";

    private readonly OutputPlanner _planner;
    private readonly IPdfRenderer _renderer;

    public GrayscaleConverter(OutputPlanner planner, IPdfRenderer renderer)
    {
        _planner = planner;
        _renderer = renderer;
    }

    public async Task<GrayscaleResult> ConvertAsync(string path, OutputTarget target, IProgress<double>? progress = null, CancellationToken ct = default,
        ISet<string>? reserved = null)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new PdfEngineException($"Arquivo não encontrado: {full}");
        var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(full) + "_cinza.pdf", target, OperationName, [full], reserved);
        var converted = await Task.Run(() => Convert(full, output, overwrite, progress, ct), ct).ConfigureAwait(false);

        // Conferência visual: renderiza cada página e procura pixels coloridos.
        var colored = new List<int>();
        var pages = Enumerable.Range(0, converted.File.PageCount).ToList();
        await _renderer.RenderPagesAsync(output, pages, RenderSize.Fit(200, 280), (index, page) =>
        {
            if (HasColor(page)) lock (colored) colored.Add(index + 1);
        }, ct).ConfigureAwait(false);
        colored.Sort();
        progress?.Report(1);
        return converted with { PagesStillInColor = colored };
    }

    private static bool HasColor(RenderedPage page)
    {
        long colored = 0;
        var total = (long)page.Width * page.Height;
        for (var i = 0; i + 3 < page.Bgra.Length; i += 4)
        {
            int b = page.Bgra[i], g = page.Bgra[i + 1], r = page.Bgra[i + 2];
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 24) colored++;
        }

        return colored > total / 2000;
    }

    private static GrayscaleResult Convert(string full, string output, bool overwrite, IProgress<double>? progress, CancellationToken ct)
    {
        PdfDocument document;
        try
        {
            document = PdfReader.Open(full, PdfDocumentOpenMode.Modify);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase)
                ? "está protegido por senha (remova a proteção antes, em \"Remover proteção\")"
                : "não é um PDF válido ou está danificado";
            throw new PdfEngineException($"{Path.GetFileName(full)} não pôde ser aberto: {reason}.", ex);
        }

        using (document)
        {
            var notes = new SortedSet<string>(StringComparer.Ordinal);
            int operators = 0, images = 0, shadings = 0;
            var done = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

            // 1. Conteúdo das páginas (os vários streams de uma página formam um só conteúdo).
            for (var p = 0; p < document.PageCount; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = document.Pages[p];
                var resources = page.Elements.GetDictionary("/Resources") ?? Inherited(page);
                var state = new ColorState();
                var contents = Resolve(page.Elements["/Contents"]);
                var streams = contents switch
                {
                    PdfArray array => array.Elements.Select(e => Resolve(e) as PdfDictionary).OfType<PdfDictionary>().ToList(),
                    PdfDictionary single => [single],
                    _ => [],
                };
                foreach (var stream in streams)
                {
                    if (!done.Add(stream)) continue;
                    operators += RewriteStream(stream, resources, state, notes);
                }

                // Cores próprias dos comentários (usadas quando não há aparência gravada).
                if (page.Elements.GetArray("/Annots") is { } annotations)
                {
                    foreach (var item in annotations.Elements)
                    {
                        if (Resolve(item) is PdfDictionary annotation)
                        {
                            foreach (var key in new[] { "/C", "/IC" })
                                if (annotation.Elements.GetArray(key) is { } color && ToGray(color) is { } gray) annotation.Elements[key] = gray;
                        }
                    }
                }

                progress?.Report(0.3 * (p + 1) / document.PageCount);
            }

            // 2. Formulários XObject, aparências de comentários e padrões de ladrilho (todos com conteúdo próprio).
            var all = document.Internals.GetAllObjects().OfType<PdfDictionary>().ToList();
            foreach (var dictionary in all)
            {
                ct.ThrowIfCancellationRequested();
                if (dictionary.Stream is null || done.Contains(dictionary)) continue;
                var isForm = dictionary.Elements.GetName("/Subtype") == "/Form";
                var isTiling = dictionary.Elements.GetInteger("/PatternType") == 1;
                if (!isForm && !isTiling) continue;
                done.Add(dictionary);
                operators += RewriteStream(dictionary, dictionary.Elements.GetDictionary("/Resources"), new ColorState(), notes);
            }

            progress?.Report(0.5);

            // 3. Imagens e degradês.
            foreach (var dictionary in all)
            {
                ct.ThrowIfCancellationRequested();
                if (dictionary.Elements.GetName("/Subtype") == "/Image")
                {
                    if (ConvertImage(dictionary, notes)) images++;
                }
                else if (dictionary.Elements.ContainsKey("/ShadingType"))
                {
                    if (ConvertShading(dictionary, notes)) shadings++;
                }
            }

            // Degradês dentro de padrões (/PatternType 2) podem ser dicionários diretos.
            foreach (var dictionary in all.Where(d => d.Elements.GetInteger("/PatternType") == 2))
            {
                if (Resolve(dictionary.Elements["/Shading"]) is PdfDictionary shading && !shading.IsIndirect && ConvertShading(shading, notes)) shadings++;
            }

            progress?.Report(0.8);
            document.Options.CompressContentStreams = true;
            var pageCount = document.PageCount;
            var written = OutputPlanner.WriteAtomically(output, overwrite, temp => document.Save(temp));
            return new GrayscaleResult(new ProducedFile(output, written, pageCount), operators, images, shadings, [], notes.ToList());
        }
    }

    private static PdfDictionary? Inherited(PdfDictionary page)
    {
        for (var parent = page.Elements.GetDictionary("/Parent"); parent is not null; parent = parent.Elements.GetDictionary("/Parent"))
            if (parent.Elements.GetDictionary("/Resources") is { } resources) return resources;
        return null;
    }

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    // ---------------- Conteúdo ----------------

    /// <summary>Componentes de cor do espaço atual de preenchimento e de traço (0 = não convertido).</summary>
    private sealed class ColorState
    {
        public int Fill { get; set; } = 1;
        public int Stroke { get; set; } = 1;
        public Stack<(int, int)> Saved { get; } = new();
    }

    private static int RewriteStream(PdfDictionary stream, PdfDictionary? resources, ColorState state, ISet<string> notes)
    {
        var data = stream.Stream?.UnfilteredValue;
        if (data is null || data.Length == 0) return 0;
        var (rewritten, count) = Rewrite(data, name => SpaceComponents(resources, name), state, notes);
        if (count == 0) return 0;
        SetFlate(stream, rewritten);
        return count;
    }

    /// <summary>Reescreve os operadores de cor de um stream de conteúdo; o resto é copiado byte a byte.</summary>
    private static (byte[] Data, int Count) Rewrite(byte[] data, Func<string, int> components, ColorState state, ISet<string> notes)
    {
        var output = new MemoryStream(data.Length + 64);
        var operands = new List<(int Start, int End, string Text)>();
        var copied = 0;
        var count = 0;
        var lexer = new ContentLexer(data);
        while (lexer.Next() is { } token)
        {
            if (token.Kind != TokenKind.Operator)
            {
                operands.Add((token.Start, token.End, token.Text));
                continue;
            }

            string? replacement = null;
            var op = token.Text;
            switch (op)
            {
                case "q":
                    state.Saved.Push((state.Fill, state.Stroke));
                    break;
                case "Q":
                    if (state.Saved.Count > 0) (state.Fill, state.Stroke) = state.Saved.Pop();
                    break;
                case "g":
                    state.Fill = 1;
                    break;
                case "G":
                    state.Stroke = 1;
                    break;
                case "rg" or "RG" or "k" or "K":
                {
                    var values = Numbers(operands);
                    var expected = op is "rg" or "RG" ? 3 : 4;
                    if (values is not null && values.Length == expected)
                    {
                        replacement = Format(Luminance(values)) + (op is "rg" or "k" ? " g" : " G");
                        if (op is "rg" or "k") state.Fill = 1;
                        else state.Stroke = 1;
                    }

                    break;
                }

                case "cs" or "CS":
                {
                    var n = operands.Count == 1 && operands[0].Text.StartsWith('/') ? components(operands[0].Text) : 0;
                    if (n is 3 or 4)
                    {
                        replacement = "/DeviceGray " + op;
                        n = -n; // espaço convertido: os próximos "sc" com n valores viram um
                    }
                    else if (n == 0)
                    {
                        notes.Add("Cores especiais (separação, DeviceN, padrões ou Lab) foram mantidas como estão.");
                    }

                    if (op == "cs") state.Fill = n;
                    else state.Stroke = n;
                    break;
                }

                case "sc" or "scn" or "SC" or "SCN":
                {
                    var current = op is "sc" or "scn" ? state.Fill : state.Stroke;
                    var values = Numbers(operands);
                    if (current < 0 && values is not null && values.Length == -current)
                        replacement = Format(Luminance(values)) + " " + op;
                    break;
                }

                case "BI":
                    notes.Add("Imagens embutidas no conteúdo (inline) foram mantidas como estão.");
                    break;
            }

            if (replacement is not null)
            {
                var start = operands.Count > 0 ? operands[0].Start : token.Start;
                output.Write(data, copied, start - copied);
                output.Write(Encoding.ASCII.GetBytes(replacement));
                copied = token.End;
                count++;
            }

            operands.Clear();
        }

        if (count == 0) return (data, 0);
        output.Write(data, copied, data.Length - copied);
        return (output.ToArray(), count);
    }

    private static double[]? Numbers(List<(int Start, int End, string Text)> operands)
    {
        var values = new double[operands.Count];
        for (var i = 0; i < operands.Count; i++)
        {
            if (!double.TryParse(operands[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return null;
        }

        return values;
    }

    private static double Luminance(double[] values)
    {
        double r, g, b;
        if (values.Length == 4)
        {
            var k = Math.Clamp(values[3], 0, 1);
            r = (1 - Math.Clamp(values[0], 0, 1)) * (1 - k);
            g = (1 - Math.Clamp(values[1], 0, 1)) * (1 - k);
            b = (1 - Math.Clamp(values[2], 0, 1)) * (1 - k);
        }
        else
        {
            (r, g, b) = (Math.Clamp(values[0], 0, 1), Math.Clamp(values[1], 0, 1), Math.Clamp(values[2], 0, 1));
        }

        return 0.299 * r + 0.587 * g + 0.114 * b;
    }

    private static string Format(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Componentes de um espaço de cor citado num operador: 3 = RGB, 4 = CMYK, 1 = cinza, 0 = outro.</summary>
    private static int SpaceComponents(PdfDictionary? resources, string name)
    {
        switch (name)
        {
            case "/DeviceRGB" or "/RGB" or "/CalRGB": return 3;
            case "/DeviceCMYK" or "/CMYK": return 4;
            case "/DeviceGray" or "/G" or "/CalGray": return 1;
        }

        var space = Resolve(resources?.Elements.GetDictionary("/ColorSpace")?.Elements[name]);
        return SpaceComponents(space);
    }

    private static int SpaceComponents(PdfItem? space)
    {
        space = Resolve(space);
        if (space is PdfName single) return SpaceComponents(null, single.Value);
        if (space is not PdfArray { Elements.Count: >= 1 } array || Resolve(array.Elements[0]) is not PdfName kind) return 0;
        return kind.Value switch
        {
            "/ICCBased" when array.Elements.Count > 1 && Resolve(array.Elements[1]) is PdfDictionary profile => profile.Elements.GetInteger("/N") switch
            {
                1 => 1,
                3 => 3,
                4 => 4,
                _ => 0,
            },
            "/CalRGB" => 3,
            "/CalGray" => 1,
            _ => 0,
        };
    }

    private static PdfArray? ToGray(PdfArray color)
    {
        var values = color.Elements.Select(e => Resolve(e) switch
        {
            PdfReal real => real.Value,
            PdfInteger integer => integer.Value,
            _ => double.NaN,
        }).ToArray();
        if (values.Length is not (3 or 4) || values.Any(double.IsNaN)) return null;
        var result = new PdfArray();
        result.Elements.Add(new PdfReal(Math.Round(Luminance(values), 4)));
        return result;
    }

    private static void SetFlate(PdfDictionary dictionary, byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(data, 0, data.Length);
        dictionary.Stream!.Value = buffer.ToArray();
        dictionary.Elements.SetName("/Filter", "/FlateDecode");
        dictionary.Elements.Remove("/DecodeParms");
        dictionary.Elements.SetInteger("/Length", (int)buffer.Length);
    }

    // ---------------- Imagens ----------------

    private static bool ConvertImage(PdfDictionary image, ISet<string> notes)
    {
        if (image.Elements.GetBoolean("/ImageMask") || image.Stream?.Value is null) return false;
        var space = Resolve(image.Elements["/ColorSpace"]);
        var components = SpaceComponents(space);
        var indexed = space is PdfArray { Elements.Count: 4 } idx && Resolve(idx.Elements[0]) is PdfName { Value: "/Indexed" or "/I" };
        if (components == 1 || space is null) return false;
        try
        {
            if (indexed) return ConvertIndexed(image, (PdfArray)space!, notes);
            if (components == 0)
            {
                notes.Add("Imagens em espaços de cor especiais foram mantidas como estão.");
                return false;
            }

            if (image.Elements["/Decode"] is not null)
            {
                notes.Add("Imagens com tabela de decodificação própria foram mantidas como estão.");
                return false;
            }

            var filter = Resolve(image.Elements["/Filter"]) switch
            {
                PdfName name => name.Value,
                PdfArray { Elements.Count: 1 } array => (Resolve(array.Elements[0]) as PdfName)?.Value,
                null => null,
                _ => "?",
            };
            var width = image.Elements.GetInteger("/Width");
            var height = image.Elements.GetInteger("/Height");
            if (width <= 0 || height <= 0) return false;

            if (filter == "/DCTDecode")
            {
                var decoder = new JpegBitmapDecoder(new MemoryStream(image.Stream.Value),
                    BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                BitmapSource frame = decoder.Frames[0];
                if (frame.Format == PixelFormats.Cmyk32)
                {
                    // JPEGs CMYK de programas Adobe costumam vir invertidos; sem como confirmar, ficam como estão.
                    notes.Add("Imagens JPEG em CMYK foram mantidas como estão.");
                    return false;
                }

                var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
                var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
                encoder.Frames.Add(BitmapFrame.Create(gray));
                using var buffer = new MemoryStream();
                encoder.Save(buffer);
                image.Stream.Value = buffer.ToArray();
                image.Elements.SetName("/Filter", "/DCTDecode");
                image.Elements.Remove("/DecodeParms");
                image.Elements.SetInteger("/Length", (int)buffer.Length);
                image.Elements.SetName("/ColorSpace", "/DeviceGray");
                image.Elements.SetInteger("/BitsPerComponent", 8);
                return true;
            }

            if (filter is null or "/FlateDecode" or "/LZWDecode" or "/RunLengthDecode" && image.Elements.GetInteger("/BitsPerComponent") == 8)
            {
                var raw = image.Stream.UnfilteredValue;
                if (raw is null || raw.Length < (long)width * height * components) return false;
                var gray = new byte[width * height];
                for (var i = 0; i < gray.Length; i++)
                {
                    var o = i * components;
                    gray[i] = components == 3
                        ? (byte)Math.Round(0.299 * raw[o] + 0.587 * raw[o + 1] + 0.114 * raw[o + 2])
                        : (byte)Math.Round(255 * Luminance([raw[o] / 255.0, raw[o + 1] / 255.0, raw[o + 2] / 255.0, raw[o + 3] / 255.0]));
                }

                SetFlate(image, gray);
                image.Elements.SetName("/ColorSpace", "/DeviceGray");
                return true;
            }

            notes.Add("Imagens em JPEG 2000 ou com menos de 8 bits por cor foram mantidas como estão.");
            return false;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException
                                       or IOException or System.Runtime.InteropServices.COMException)
        {
            notes.Add("Algumas imagens não puderam ser lidas e foram mantidas como estão.");
            return false;
        }
    }

    /// <summary>Imagem indexada: só a paleta muda (cada cor vira o cinza equivalente).</summary>
    private static bool ConvertIndexed(PdfDictionary image, PdfArray space, ISet<string> notes)
    {
        var baseComponents = SpaceComponents(space.Elements[1]);
        if (baseComponents == 1) return false;
        if (baseComponents == 0)
        {
            notes.Add("Imagens indexadas com paleta em espaço de cor especial foram mantidas como estão.");
            return false;
        }

        var hival = (Resolve(space.Elements[2]) as PdfInteger)?.Value ?? -1;
        var lookup = Resolve(space.Elements[3]) switch
        {
            PdfString text => text.Value.Select(c => (byte)c).ToArray(),
            PdfDictionary { Stream: { } stream } => stream.UnfilteredValue,
            _ => null,
        };
        if (hival < 0 || lookup is null || lookup.Length < (hival + 1) * baseComponents) return false;
        var gray = new StringBuilder(hival + 1);
        for (var i = 0; i <= hival; i++)
        {
            var o = i * baseComponents;
            var values = Enumerable.Range(0, baseComponents).Select(c => lookup[o + c] / 255.0).ToArray();
            gray.Append((char)(byte)Math.Round(255 * Luminance(values)));
        }

        var replaced = new PdfArray();
        replaced.Elements.Add(new PdfName("/Indexed"));
        replaced.Elements.Add(new PdfName("/DeviceGray"));
        replaced.Elements.Add(new PdfInteger(hival));
        replaced.Elements.Add(new PdfString(gray.ToString(), PdfStringEncoding.RawEncoding));
        image.Elements["/ColorSpace"] = replaced;
        return true;
    }

    // ---------------- Degradês ----------------

    /// <summary>Degradês com função exponencial (tipo 2) em RGB/CMYK: as cores das pontas viram cinza.</summary>
    private static bool ConvertShading(PdfDictionary shading, ISet<string> notes)
    {
        var components = SpaceComponents(shading.Elements["/ColorSpace"]);
        if (components is 1) return false;
        if (components == 0 || Resolve(shading.Elements["/Function"]) is not PdfDictionary function || function.Elements.GetInteger("/FunctionType") != 2)
        {
            notes.Add("Alguns degradês complexos foram mantidos em cores.");
            return false;
        }

        foreach (var key in new[] { "/C0", "/C1" })
        {
            var color = function.Elements.GetArray(key);
            var gray = color is null ? null : ToGray(color);
            if (color is not null && gray is null) return false;
            if (gray is not null) function.Elements[key] = gray;
        }

        if (function.Elements.GetArray("/C0") is null)
        {
            var zero = new PdfArray();
            zero.Elements.Add(new PdfReal(0));
            function.Elements["/C0"] = zero;
        }

        if (function.Elements.GetArray("/C1") is null)
        {
            var one = new PdfArray();
            one.Elements.Add(new PdfReal(1));
            function.Elements["/C1"] = one;
        }

        function.Elements.Remove("/Range");
        shading.Elements.SetName("/ColorSpace", "/DeviceGray");
        return true;
    }

    // ---------------- Leitor de conteúdo ----------------

    private enum TokenKind
    {
        Operand,
        Operator,
    }

    private readonly record struct Token(TokenKind Kind, int Start, int End, string Text);

    /// <summary>
    /// Lê os elementos de um stream de conteúdo (números, nomes, textos, listas, dicionários e
    /// operadores), pulando imagens embutidas (BI … ID dados EI) sem interpretá-las.
    /// </summary>
    private sealed class ContentLexer(byte[] data)
    {
        private int _position;

        public Token? Next()
        {
            SkipSpaceAndComments();
            if (_position >= data.Length) return null;
            var start = _position;
            var c = data[_position];
            switch (c)
            {
                case (byte)'(':
                    SkipLiteralString();
                    return Operand(start);
                case (byte)'<' when Peek(1) == '<':
                    SkipDictionary();
                    return Operand(start);
                case (byte)'<':
                    while (_position < data.Length && data[_position] != '>') _position++;
                    _position = Math.Min(data.Length, _position + 1);
                    return Operand(start);
                case (byte)'[':
                    SkipArray();
                    return Operand(start);
                case (byte)'/':
                    _position++;
                    while (_position < data.Length && !IsDelimiter(data[_position]) && !IsSpace(data[_position])) _position++;
                    return Operand(start);
            }

            if (IsDelimiter(c))
            {
                _position++;
                return Operand(start);
            }

            while (_position < data.Length && !IsDelimiter(data[_position]) && !IsSpace(data[_position])) _position++;
            var text = Encoding.ASCII.GetString(data, start, _position - start);
            var isNumber = text.Length > 0 && (char.IsDigit(text[0]) || text[0] is '-' or '+' or '.');
            if (isNumber) return new Token(TokenKind.Operand, start, _position, text);
            if (text == "BI") SkipInlineImage();
            return new Token(TokenKind.Operator, start, text == "BI" ? _position : start + text.Length, text);
        }

        private Token Operand(int start) => new(TokenKind.Operand, start, _position, Encoding.ASCII.GetString(data, start, _position - start));

        private int Peek(int offset) => _position + offset < data.Length ? data[_position + offset] : -1;

        private void SkipSpaceAndComments()
        {
            while (_position < data.Length)
            {
                if (IsSpace(data[_position])) _position++;
                else if (data[_position] == '%')
                {
                    while (_position < data.Length && data[_position] is not ((byte)'\n' or (byte)'\r')) _position++;
                }
                else break;
            }
        }

        private void SkipLiteralString()
        {
            var depth = 0;
            while (_position < data.Length)
            {
                var c = data[_position++];
                if (c == '\\') _position++;
                else if (c == '(') depth++;
                else if (c == ')' && --depth == 0) return;
            }
        }

        private void SkipArray()
        {
            _position++;
            while (true)
            {
                SkipSpaceAndComments();
                if (_position >= data.Length) return;
                if (data[_position] == ']')
                {
                    _position++;
                    return;
                }

                Next();
            }
        }

        private void SkipDictionary()
        {
            _position += 2;
            while (true)
            {
                SkipSpaceAndComments();
                if (_position >= data.Length) return;
                if (data[_position] == '>' && Peek(1) == '>')
                {
                    _position += 2;
                    return;
                }

                Next();
            }
        }

        /// <summary>Depois de "BI": pula o dicionário, "ID", os dados binários e "EI".</summary>
        private void SkipInlineImage()
        {
            while (true)
            {
                SkipSpaceAndComments();
                if (_position >= data.Length) return;
                if (data[_position] == 'I' && Peek(1) == 'D' && (Peek(2) == -1 || IsSpace((byte)Peek(2))))
                {
                    _position += 3;
                    break;
                }

                Next();
            }

            // Os dados terminam em "EI" cercado de espaços.
            while (_position + 1 < data.Length)
            {
                if (data[_position] == 'E' && data[_position + 1] == 'I' &&
                    (_position == 0 || IsSpace(data[_position - 1])) &&
                    (_position + 2 >= data.Length || IsSpace(data[_position + 2]) || IsDelimiter(data[_position + 2])))
                {
                    _position += 2;
                    return;
                }

                _position++;
            }

            _position = data.Length;
        }

        private static bool IsSpace(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

        private static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}'
            or (byte)'/' or (byte)'%';
    }
}
