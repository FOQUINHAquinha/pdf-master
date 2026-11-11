using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PDFMaster.Editor;

public enum FormFieldType
{
    Text,
    CheckBox,
    Radio,
    Choice,
    Button,
    Signature,
}

/// <summary>Campo de um formulário PDF existente.</summary>
/// <param name="Options">Caixa de seleção: o valor "marcado"; botões de opção e listas: as opções.</param>
public sealed record FormFieldInfo(string Name, FormFieldType Type, string? Value, IReadOnlyList<string> Options, bool ReadOnly, bool Multiline, int PageIndex)
{
    public bool IsFillable => !ReadOnly && Type is FormFieldType.Text or FormFieldType.CheckBox or FormFieldType.Radio or FormFieldType.Choice;
}

/// <summary>Leitura, preenchimento e criação de campos de formulário (AcroForm).</summary>
public static class FormFields
{
    private const int ReadOnlyFlag = 1;
    private const int MultilineFlag = 1 << 12;
    private const int RadioFlag = 1 << 15;
    private const int PushButtonFlag = 1 << 16;
    private const int ComboFlag = 1 << 17;

    // ---------- Leitura ----------

    public static IReadOnlyList<FormFieldInfo> Read(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return Read(document);
    }

    public static IReadOnlyList<FormFieldInfo> Read(PdfDocument document)
    {
        var pages = WidgetPages(document);
        return Terminals(document).Select(t => Describe(t, pages)).ToList();
    }

    /// <summary>Verdadeiro quando o formulário também tem a versão XFA (formulário híbrido).</summary>
    public static bool HasXfa(PdfDocument document) =>
        document.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.ContainsKey("/XFA") == true;

    private sealed record Terminal(PdfDictionary Field, string Name, string Type, int Flags, IReadOnlyList<PdfDictionary> Widgets);

    private static IEnumerable<Terminal> Terminals(PdfDocument document)
    {
        var acro = document.Internals.Catalog.Elements.GetDictionary("/AcroForm");
        var fields = acro?.Elements.GetArray("/Fields");
        if (fields is null) yield break;
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < fields.Elements.Count; i++)
        {
            if (fields.Elements.GetDictionary(i) is { } field)
            {
                foreach (var terminal in Walk(field, string.Empty, string.Empty, 0, visited)) yield return terminal;
            }
        }
    }

    private static IEnumerable<Terminal> Walk(PdfDictionary node, string parentName, string type, int flags, HashSet<PdfDictionary> visited)
    {
        if (!visited.Add(node)) yield break;
        var partial = node.Elements.GetString("/T");
        var name = partial.Length == 0 ? parentName : parentName.Length == 0 ? partial : parentName + "." + partial;
        if (node.Elements.ContainsKey("/FT")) type = node.Elements.GetName("/FT");
        if (node.Elements.ContainsKey("/Ff")) flags = node.Elements.GetInteger("/Ff");

        var kids = node.Elements.GetArray("/Kids");
        var children = new List<PdfDictionary>();
        if (kids is not null)
        {
            for (var i = 0; i < kids.Elements.Count; i++)
            {
                if (kids.Elements.GetDictionary(i) is { } kid) children.Add(kid);
            }
        }

        var subfields = children.Where(k => k.Elements.ContainsKey("/T")).ToList();
        if (subfields.Count > 0)
        {
            foreach (var kid in subfields)
            {
                foreach (var terminal in Walk(kid, name, type, flags, visited)) yield return terminal;
            }

            yield break;
        }

        var widgets = children.Count > 0 ? children : [node];
        yield return new Terminal(node, name, type, flags, widgets);
    }

    private static Dictionary<int, int> WidgetPages(PdfDocument document)
    {
        var map = new Dictionary<int, int>();
        for (var p = 0; p < document.PageCount; p++)
        {
            var annots = document.Pages[p].Elements.GetArray("/Annots");
            if (annots is null) continue;
            for (var i = 0; i < annots.Elements.Count; i++)
            {
                if (annots.Elements[i] is PdfReference reference) map.TryAdd(reference.ObjectID.ObjectNumber, p);
            }
        }

        return map;
    }

    private static FormFieldInfo Describe(Terminal t, Dictionary<int, int> pages)
    {
        var page = t.Widgets.Select(w => w.Reference is { } r && pages.TryGetValue(r.ObjectID.ObjectNumber, out var p) ? p : -1).FirstOrDefault(p => p >= 0, -1);
        var readOnly = (t.Flags & ReadOnlyFlag) != 0;
        var value = ValueOf(t.Field);
        switch (t.Type)
        {
            case "/Tx":
                return new FormFieldInfo(t.Name, FormFieldType.Text, value, [], readOnly, (t.Flags & MultilineFlag) != 0, page);
            case "/Ch":
                return new FormFieldInfo(t.Name, FormFieldType.Choice, value, ChoiceOptions(t.Field), readOnly, false, page);
            case "/Sig":
                return new FormFieldInfo(t.Name, FormFieldType.Signature, null, [], true, false, page);
            case "/Btn" when (t.Flags & PushButtonFlag) != 0:
                return new FormFieldInfo(t.Name, FormFieldType.Button, null, [], true, false, page);
            case "/Btn" when (t.Flags & RadioFlag) != 0:
                var states = RadioStates(t);
                var selected = states.FirstOrDefault(s => s.State == value).Label ?? (value == "Off" ? null : value);
                return new FormFieldInfo(t.Name, FormFieldType.Radio, selected, states.Select(s => s.Label).ToList(), readOnly, false, page);
            case "/Btn":
                var on = t.Widgets.SelectMany(OnStates).FirstOrDefault() ?? "Yes";
                return new FormFieldInfo(t.Name, FormFieldType.CheckBox, value is null || value == "Off" ? "Off" : on, [on], readOnly, false, page);
            default:
                return new FormFieldInfo(t.Name, FormFieldType.Text, value, [], true, false, page);
        }
    }

    private static string? ValueOf(PdfDictionary field)
    {
        for (var node = field; node is not null; node = node.Elements.GetDictionary("/Parent"))
        {
            var item = node.Elements["/V"];
            if (item is PdfReference r) item = r.Value;
            switch (item)
            {
                case PdfString s:
                    return s.Value;
                case PdfName n:
                    return n.Value.TrimStart('/');
                case PdfArray a when a.Elements.Count > 0:
                    return a.Elements[0] is PdfString first ? first.Value : null;
            }
        }

        return null;
    }

    private static IEnumerable<string> OnStates(PdfDictionary widget)
    {
        var normal = widget.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
        return normal is null ? [] : normal.Elements.Keys.Select(k => k.TrimStart('/')).Where(k => k != "Off");
    }

    /// <summary>Estado de cada botão de opção e o texto mostrado (do /Opt, quando existe).</summary>
    private static List<(string State, string Label)> RadioStates(Terminal t)
    {
        var options = t.Field.Elements.GetArray("/Opt");
        var result = new List<(string, string)>();
        for (var i = 0; i < t.Widgets.Count; i++)
        {
            var state = OnStates(t.Widgets[i]).FirstOrDefault();
            if (state is null || result.Any(r => r.Item1 == state)) continue;
            var label = options is not null && i < options.Elements.Count && options.Elements[i] is PdfString s ? s.Value : state;
            result.Add((state, label));
        }

        return result;
    }

    private static IReadOnlyList<string> ChoiceOptions(PdfDictionary field)
    {
        var options = field.Elements.GetArray("/Opt");
        if (options is null) return [];
        var result = new List<string>();
        for (var i = 0; i < options.Elements.Count; i++)
        {
            var item = options.Elements[i];
            if (item is PdfReference r) item = r.Value;
            switch (item)
            {
                case PdfString s:
                    result.Add(s.Value);
                    break;
                case PdfArray pair when pair.Elements.Count >= 2 && pair.Elements[1] is PdfString display:
                    result.Add(display.Value);
                    break;
            }
        }

        return result;
    }

    // ---------- Preenchimento ----------

    /// <summary>
    /// Grava os valores (nome completo → valor; caixa de seleção: "Off" ou o valor marcado) e
    /// refaz a aparência dos campos de texto e listas. Devolve os nomes que não puderam ser preenchidos.
    /// </summary>
    public static IReadOnlyList<string> Fill(PdfDocument document, IReadOnlyDictionary<string, string?> values)
    {
        var skipped = new List<string>();
        var helv = HelveticaResources(document);
        foreach (var t in Terminals(document).ToList())
        {
            if (!values.TryGetValue(t.Name, out var value)) continue;
            if ((t.Flags & ReadOnlyFlag) != 0)
            {
                skipped.Add(t.Name);
                continue;
            }

            switch (t.Type)
            {
                case "/Tx":
                    t.Field.Elements["/V"] = PdfObjects.Text(value ?? string.Empty);
                    foreach (var widget in t.Widgets) TextWidgetAppearance(document, widget, t.Field, value ?? string.Empty, (t.Flags & MultilineFlag) != 0, helv);
                    break;
                case "/Ch":
                    t.Field.Elements["/V"] = PdfObjects.Text(value ?? string.Empty);
                    t.Field.Elements.Remove("/I");
                    foreach (var widget in t.Widgets) TextWidgetAppearance(document, widget, t.Field, value ?? string.Empty, false, helv, (t.Flags & ComboFlag) != 0);
                    break;
                case "/Btn" when (t.Flags & PushButtonFlag) != 0:
                    skipped.Add(t.Name);
                    break;
                case "/Btn" when (t.Flags & RadioFlag) != 0:
                    var states = RadioStates(t);
                    var chosen = states.FirstOrDefault(s => s.Label == value || s.State == value).State;
                    t.Field.Elements["/V"] = new PdfName("/" + (chosen ?? "Off"));
                    foreach (var widget in t.Widgets)
                        widget.Elements["/AS"] = new PdfName("/" + (chosen is not null && OnStates(widget).Contains(chosen) ? chosen : "Off"));
                    break;
                case "/Btn":
                    var check = !string.IsNullOrEmpty(value) && value != "Off" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
                    var onState = t.Widgets.SelectMany(OnStates).FirstOrDefault() ?? "Yes";
                    t.Field.Elements["/V"] = new PdfName("/" + (check ? onState : "Off"));
                    foreach (var widget in t.Widgets) widget.Elements["/AS"] = new PdfName("/" + (check && OnStates(widget).Contains(onState) ? onState : "Off"));
                    break;
                default:
                    skipped.Add(t.Name);
                    break;
            }
        }

        // Formulário híbrido: sem isso, leitores que preferem o XFA ignorariam os valores gravados.
        document.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.Remove("/XFA");
        return skipped;
    }

    private static void TextWidgetAppearance(PdfDocument document, PdfDictionary widget, PdfDictionary field, string value, bool multiline, PdfDictionary resources, bool combo = false)
    {
        var rect = widget.Elements.GetArray("/Rect");
        if (rect is null || rect.Elements.Count != 4) return;
        var w = Math.Abs(rect.Elements.GetReal(2) - rect.Elements.GetReal(0));
        var h = Math.Abs(rect.Elements.GetReal(3) - rect.Elements.GetReal(1));
        var rotation = widget.Elements.GetDictionary("/MK")?.Elements.GetInteger("/R") ?? 0;
        if (rotation is 90 or 270) (w, h) = (h, w);

        var da = widget.Elements.GetString("/DA");
        if (da.Length == 0) da = field.Elements.GetString("/DA");
        var size = FontSizeFromDa(da);
        var quadding = field.Elements.GetInteger("/Q");
        var content = TextContent(w, h, value, size, quadding, multiline, combo);
        var form = PdfObjects.ManualForm(document, w, h, content, resources);
        if (rotation != 0) form.Elements["/Matrix"] = PdfObjects.Matrix(document, RotationMatrix(rotation, w, h));
        var ap = new PdfDictionary(document);
        ap.Elements["/N"] = form.Reference!;
        widget.Elements["/AP"] = ap;
    }

    private static Affine RotationMatrix(int degrees, double w, double h) => degrees switch
    {
        90 => new Affine(0, 1, -1, 0, h, 0),
        180 => new Affine(-1, 0, 0, -1, w, h),
        270 => new Affine(0, -1, 1, 0, 0, w),
        _ => Affine.Identity,
    };

    private static double FontSizeFromDa(string da)
    {
        var parts = da.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(parts, "Tf");
        return index >= 1 && double.TryParse(parts[index - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var size) ? size : 0;
    }

    // ---------- Criação ----------

    internal static void Create(PdfDocument document, PdfPage page, PageFrame frame, FieldItem item)
    {
        var acro = AcroForm(document);
        var fields = acro.Elements.GetArray("/Fields")!;
        var name = UniqueName(document, Clean(item.Name));
        var helv = HelveticaResources(document);
        var rect = item.Rect;
        var fontSize = item.FontSize > 0 ? item.FontSize : 0;
        var da = $"/Helv {Pdf.Number(fontSize)} Tf 0 g";

        PdfDictionary Widget(ViewRect r)
        {
            var widget = PdfObjects.Annotation(document, page, "/Widget", frame.PdfRect(r));
            var mk = new PdfDictionary(document);
            mk.Elements["/BC"] = PdfObjects.Numbers(document, 0.25, 0.25, 0.25);
            mk.Elements["/BG"] = PdfObjects.Numbers(document, 1, 1, 1);
            if (frame.Rotation != 0) mk.Elements["/R"] = new PdfInteger(frame.Rotation);
            widget.Elements["/MK"] = mk;
            return widget;
        }

        PdfDictionary Appearance(ViewRect r, string content)
        {
            var form = PdfObjects.ManualForm(document, r.Width, r.Height, content, helv);
            form.Elements["/Matrix"] = PdfObjects.Matrix(document, frame.FormToPdf(r).Linear);
            return form;
        }

        switch (item.Field)
        {
            case FieldKind.Text or FieldKind.MultilineText or FieldKind.Date:
            {
                var widget = Widget(rect);
                widget.Elements["/FT"] = new PdfName("/Tx");
                widget.Elements["/T"] = PdfObjects.Text(name);
                widget.Elements["/DA"] = new PdfString(da);
                var multiline = item.Field == FieldKind.MultilineText;
                if (multiline) widget.Elements["/Ff"] = new PdfInteger(MultilineFlag);
                var value = item.Value ?? string.Empty;
                if (value.Length > 0) widget.Elements["/V"] = PdfObjects.Text(value);
                if (item.Field == FieldKind.Date)
                {
                    widget.Elements["/TU"] = PdfObjects.Text("Data (dd/mm/aaaa)");
                    var aa = new PdfDictionary(document);
                    aa.Elements["/F"] = JavaScript(document, "AFDate_FormatEx(\"dd/mm/yyyy\");");
                    aa.Elements["/K"] = JavaScript(document, "AFDate_KeystrokeEx(\"dd/mm/yyyy\");");
                    widget.Elements["/AA"] = aa;
                }

                SetNormal(document, widget, Appearance(rect, TextContent(rect.Width, rect.Height, value, fontSize, 0, multiline, false)));
                fields.Elements.Add(widget.Reference!);
                break;
            }

            case FieldKind.CheckBox:
            {
                var widget = Widget(rect);
                widget.Elements["/FT"] = new PdfName("/Btn");
                widget.Elements["/T"] = PdfObjects.Text(name);
                widget.Elements["/DA"] = new PdfString("/ZaDb 0 Tf 0 g");
                var on = item.Value is { Length: > 0 } v && v != "Off";
                widget.Elements["/V"] = new PdfName(on ? "/Sim" : "/Off");
                widget.Elements["/AS"] = new PdfName(on ? "/Sim" : "/Off");
                widget.Elements.GetDictionary("/MK")!.Elements["/CA"] = new PdfString("4");
                var states = new PdfDictionary(document);
                states.Elements["/Sim"] = Appearance(rect, CheckContent(rect.Width, rect.Height, true)).Reference!;
                states.Elements["/Off"] = Appearance(rect, CheckContent(rect.Width, rect.Height, false)).Reference!;
                var ap = new PdfDictionary(document);
                ap.Elements["/N"] = states;
                widget.Elements["/AP"] = ap;
                fields.Elements.Add(widget.Reference!);
                break;
            }

            case FieldKind.Radio:
            {
                var parent = new PdfDictionary(document);
                parent.Elements["/FT"] = new PdfName("/Btn");
                parent.Elements["/Ff"] = new PdfInteger(RadioFlag | (1 << 14));
                parent.Elements["/T"] = PdfObjects.Text(name);
                document.Internals.AddObject(parent);
                var kids = new PdfArray(document);
                parent.Elements["/Kids"] = kids;
                var options = item.Options.Count > 0 ? item.Options : ["Opção 1", "Opção 2"];
                var used = new HashSet<string>(StringComparer.Ordinal);
                var buttons = (item with { Options = options }).RadioButtons();
                string? selected = null;
                for (var i = 0; i < options.Count; i++)
                {
                    var state = StateName(options[i], used);
                    if (options[i] == item.Value) selected = state;
                    var widget = Widget(buttons[i]);
                    widget.Elements["/Parent"] = parent.Reference!;
                    widget.Elements["/AS"] = new PdfName(options[i] == item.Value ? "/" + state : "/Off");
                    widget.Elements.GetDictionary("/MK")!.Elements["/CA"] = new PdfString("l");
                    var states = new PdfDictionary(document);
                    states.Elements["/" + state] = Appearance(buttons[i], RadioContent(buttons[i].Width, buttons[i].Height, true)).Reference!;
                    states.Elements["/Off"] = Appearance(buttons[i], RadioContent(buttons[i].Width, buttons[i].Height, false)).Reference!;
                    var ap = new PdfDictionary(document);
                    ap.Elements["/N"] = states;
                    widget.Elements["/AP"] = ap;
                    kids.Elements.Add(widget.Reference!);
                }

                parent.Elements["/Opt"] = new PdfArray(document, options.Select(o => (PdfItem)PdfObjects.Text(o)).ToArray());
                parent.Elements["/V"] = new PdfName("/" + (selected ?? "Off"));
                fields.Elements.Add(parent.Reference!);
                break;
            }

            case FieldKind.Dropdown:
            {
                var widget = Widget(rect);
                var options = item.Options.Count > 0 ? item.Options : [""];
                var value = item.Value is { } v && options.Contains(v) ? v : options[0];
                widget.Elements["/FT"] = new PdfName("/Ch");
                widget.Elements["/Ff"] = new PdfInteger(ComboFlag);
                widget.Elements["/T"] = PdfObjects.Text(name);
                widget.Elements["/DA"] = new PdfString(da);
                widget.Elements["/Opt"] = new PdfArray(document, options.Select(o => (PdfItem)PdfObjects.Text(o)).ToArray());
                widget.Elements["/V"] = PdfObjects.Text(value);
                SetNormal(document, widget, Appearance(rect, TextContent(rect.Width, rect.Height, value, fontSize, 0, false, true)));
                fields.Elements.Add(widget.Reference!);
                break;
            }

            default:
            {
                var widget = Widget(rect);
                var label = string.IsNullOrWhiteSpace(item.Value) ? item.Action == ButtonAction.Print ? "Imprimir" : "Limpar" : item.Value;
                widget.Elements["/FT"] = new PdfName("/Btn");
                widget.Elements["/Ff"] = new PdfInteger(PushButtonFlag);
                widget.Elements["/T"] = PdfObjects.Text(name);
                widget.Elements["/DA"] = new PdfString(da);
                var mk = widget.Elements.GetDictionary("/MK")!;
                mk.Elements["/CA"] = PdfObjects.Text(label);
                mk.Elements["/BG"] = PdfObjects.Numbers(document, 0.88, 0.88, 0.88);
                var action = new PdfDictionary(document);
                if (item.Action == ButtonAction.Print)
                {
                    action.Elements["/S"] = new PdfName("/JavaScript");
                    action.Elements["/JS"] = new PdfString("this.print({bUI: true});");
                }
                else
                {
                    action.Elements["/S"] = new PdfName("/ResetForm");
                }

                widget.Elements["/A"] = action;
                SetNormal(document, widget, Appearance(rect, ButtonContent(rect.Width, rect.Height, label, fontSize)));
                fields.Elements.Add(widget.Reference!);
                break;
            }
        }

    }

    private static void SetNormal(PdfDocument document, PdfDictionary widget, PdfDictionary form)
    {
        var ap = new PdfDictionary(document);
        ap.Elements["/N"] = form.Reference!;
        widget.Elements["/AP"] = ap;
    }

    private static PdfDictionary JavaScript(PdfDocument document, string script)
    {
        var action = new PdfDictionary(document);
        action.Elements["/S"] = new PdfName("/JavaScript");
        action.Elements["/JS"] = new PdfString(script);
        return action;
    }

    private static PdfDictionary AcroForm(PdfDocument document)
    {
        var catalog = document.Internals.Catalog;
        var acro = catalog.Elements.GetDictionary("/AcroForm");
        if (acro is null)
        {
            acro = new PdfDictionary(document);
            document.Internals.AddObject(acro);
            catalog.Elements["/AcroForm"] = acro.Reference!;
        }

        if (acro.Elements.GetArray("/Fields") is null) acro.Elements["/Fields"] = new PdfArray(document);
        var fonts = PdfObjects.SubDictionary(document, PdfObjects.SubDictionary(document, acro, "/DR"), "/Font");
        if (!fonts.Elements.ContainsKey("/Helv")) fonts.Elements["/Helv"] = StandardFont(document, "Helvetica", winAnsi: true).Reference!;
        if (!fonts.Elements.ContainsKey("/ZaDb")) fonts.Elements["/ZaDb"] = StandardFont(document, "ZapfDingbats", winAnsi: false).Reference!;
        if (!acro.Elements.ContainsKey("/DA")) acro.Elements["/DA"] = new PdfString("/Helv 0 Tf 0 g");
        return acro;
    }

    private static PdfDictionary StandardFont(PdfDocument document, string baseFont, bool winAnsi)
    {
        var font = new PdfDictionary(document);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName("/" + baseFont);
        if (winAnsi) font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        document.Internals.AddObject(font);
        return font;
    }

    /// <summary>Recursos das aparências: Helvetica (fonte padrão do PDF, não incorporada) como /Helv.</summary>
    private static PdfDictionary HelveticaResources(PdfDocument document)
    {
        var resources = new PdfDictionary(document);
        var fonts = new PdfDictionary(document);
        fonts.Elements["/Helv"] = StandardFont(document, "Helvetica", winAnsi: true).Reference!;
        resources.Elements["/Font"] = fonts;
        return resources;
    }

    private static string Clean(string name)
    {
        var cleaned = new string(name.Trim().Select(c => c is '.' or '\\' or '(' or ')' ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "campo" : cleaned;
    }

    private static string UniqueName(PdfDocument document, string name)
    {
        var existing = Terminals(document).Select(t => t.Name.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
        if (!existing.Contains(name)) return name;
        for (var i = 2; ; i++)
        {
            if (!existing.Contains($"{name}_{i}")) return $"{name}_{i}";
        }
    }

    /// <summary>Nome de estado (nome PDF) a partir do texto da opção: só letras e números ASCII.</summary>
    private static string StateName(string option, HashSet<string> used)
    {
        var normalized = option.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        var name = sb.ToString().Trim('_');
        if (name.Length == 0) name = "Opcao";
        var candidate = name;
        for (var i = 2; !used.Add(candidate); i++) candidate = $"{name}_{i}";
        return candidate;
    }

    // ---------- Aparências (operadores do PDF) ----------

    /// <summary>Largura aproximada de um texto em Helvetica (para alinhar e quebrar linhas).</summary>
    internal static double HelveticaWidth(string text, double size) =>
        text.Sum(c => c switch
        {
            ' ' => 0.278,
            >= '0' and <= '9' => 0.556,
            'i' or 'j' or 'l' or '.' or ',' or ':' or ';' or '\'' or '!' or '|' => 0.25,
            'm' or 'w' or 'M' or 'W' => 0.85,
            >= 'A' and <= 'Z' => 0.68,
            _ => 0.53,
        }) * size;

    internal static string TextContent(double w, double h, string value, double size, int quadding, bool multiline, bool combo)
    {
        var sb = new StringBuilder();
        sb.Append("1 g 0 0 ").Append(Pdf.Number(w)).Append(' ').Append(Pdf.Number(h)).Append(" re f\n");
        sb.Append("0.25 G 1 w 0.5 0.5 ").Append(Pdf.Number(w - 1)).Append(' ').Append(Pdf.Number(h - 1)).Append(" re S\n");
        var arrow = combo ? Math.Min(h, 14) : 0;
        if (combo)
        {
            var cx = w - arrow / 2 - 1;
            var cy = h / 2;
            sb.Append($"0.35 g {Pdf.Number(cx - 3.5)} {Pdf.Number(cy + 2)} m {Pdf.Number(cx + 3.5)} {Pdf.Number(cy + 2)} l {Pdf.Number(cx)} {Pdf.Number(cy - 2.5)} l f\n");
        }

        if (size <= 0) size = multiline ? 10 : Math.Clamp((h - 4) * 0.7, 4, 12);
        var inner = w - 4 - arrow;
        sb.Append("/Tx BMC q 1 1 ").Append(Pdf.Number(w - 2 - arrow)).Append(' ').Append(Pdf.Number(h - 2)).Append(" re W n BT /Helv ")
            .Append(Pdf.Number(size)).Append(" Tf 0 g\n");

        var lines = multiline ? WrapHelvetica(value, size, inner) : [value.Replace('\n', ' ')];
        var y = multiline ? h - 2 - size * 0.95 : (h - size * 0.72) / 2;
        foreach (var line in lines)
        {
            var width = HelveticaWidth(line, size);
            var x = quadding switch { 1 => (w - arrow - width) / 2, 2 => w - arrow - 2 - width, _ => 2.0 };
            sb.Append("1 0 0 1 ").Append(Pdf.Number(Math.Max(2, x))).Append(' ').Append(Pdf.Number(y)).Append(" Tm ")
                .Append(Pdf.Literal(Pdf.WinAnsi(line))).Append(" Tj\n");
            y -= size * 1.15;
        }

        sb.Append("ET Q EMC\n");
        return sb.ToString();
    }

    private static List<string> WrapHelvetica(string text, double size, double width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var current = string.Empty;
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && HelveticaWidth(candidate, size) > width)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }

            lines.Add(current);
        }

        return lines;
    }

    private static string Box(double w, double h) =>
        $"1 g 0 0 {Pdf.Number(w)} {Pdf.Number(h)} re f 0.25 G 1 w 0.5 0.5 {Pdf.Number(w - 1)} {Pdf.Number(h - 1)} re S\n";

    internal static string CheckContent(double w, double h, bool on)
    {
        var sb = new StringBuilder(Box(w, h));
        if (on)
        {
            var t = Math.Max(1, Math.Min(w, h) * 0.12);
            sb.Append($"0 G {Pdf.Number(t)} w 1 J 1 j {Pdf.Number(w * 0.22)} {Pdf.Number(h * 0.52)} m {Pdf.Number(w * 0.42)} {Pdf.Number(h * 0.27)} l {Pdf.Number(w * 0.8)} {Pdf.Number(h * 0.76)} l S\n");
        }

        return sb.ToString();
    }

    internal static string RadioContent(double w, double h, bool on)
    {
        var r = Math.Min(w, h) / 2;
        var sb = new StringBuilder();
        sb.Append("1 g ").Append(Circle(w / 2, h / 2, r - 0.5)).Append(" f\n");
        sb.Append("0.25 G 1 w ").Append(Circle(w / 2, h / 2, r - 0.5)).Append(" S\n");
        if (on) sb.Append("0 g ").Append(Circle(w / 2, h / 2, r * 0.45)).Append(" f\n");
        return sb.ToString();
    }

    private static string Circle(double cx, double cy, double r)
    {
        const double k = 0.5523;
        var o = r * k;
        string P(double x, double y) => $"{Pdf.Number(x)} {Pdf.Number(y)}";
        return $"{P(cx + r, cy)} m {P(cx + r, cy + o)} {P(cx + o, cy + r)} {P(cx, cy + r)} c {P(cx - o, cy + r)} {P(cx - r, cy + o)} {P(cx - r, cy)} c " +
               $"{P(cx - r, cy - o)} {P(cx - o, cy - r)} {P(cx, cy - r)} c {P(cx + o, cy - r)} {P(cx + r, cy - o)} {P(cx + r, cy)} c";
    }

    internal static string ButtonContent(double w, double h, string label, double size)
    {
        if (size <= 0) size = Math.Clamp((h - 4) * 0.6, 4, 12);
        var width = HelveticaWidth(label, size);
        return $"0.88 g 0 0 {Pdf.Number(w)} {Pdf.Number(h)} re f 0.45 G 1 w 0.5 0.5 {Pdf.Number(w - 1)} {Pdf.Number(h - 1)} re S\n" +
               $"BT /Helv {Pdf.Number(size)} Tf 0 g 1 0 0 1 {Pdf.Number(Math.Max(2, (w - width) / 2))} {Pdf.Number((h - size * 0.72) / 2)} Tm {Pdf.Literal(Pdf.WinAnsi(label))} Tj ET\n";
    }
}
