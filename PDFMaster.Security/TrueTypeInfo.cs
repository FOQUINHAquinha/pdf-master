using System.Buffers.Binary;

namespace PDFMaster.Security;

/// <summary>
/// Leitura mínima de um programa de fonte TrueType: larguras, mapa de caracteres (cmap 3,1/3,0),
/// permissão de incorporação (OS/2 fsType) e quais glifos existem (para o CIDSet do PDF/A).
/// </summary>
internal sealed class TrueTypeInfo
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
    private ushort[]? _advances;
    private Dictionary<int, int>? _cmap;

    private TrueTypeInfo(byte[] data)
    {
        _data = data;
        var numTables = U16(4);
        for (var i = 0; i < numTables; i++)
        {
            var record = 12 + 16 * i;
            var tag = System.Text.Encoding.ASCII.GetString(data, record, 4);
            _tables[tag] = ((int)U32(record + 8), (int)U32(record + 12));
        }

        if (!_tables.ContainsKey("head") || !_tables.ContainsKey("maxp")) throw new InvalidDataException("Programa de fonte TrueType incompleto.");
        UnitsPerEm = U16(_tables["head"].Offset + 18);
        if (UnitsPerEm == 0) UnitsPerEm = 1000;
        GlyphCount = U16(_tables["maxp"].Offset + 4);
        FsType = _tables.TryGetValue("OS/2", out var os2) && os2.Length >= 10 ? U16(os2.Offset + 8) : 0;
    }

    public int UnitsPerEm { get; }

    /// <summary>Caixa de todos os glifos (xMin, yMin, xMax, yMax) em milésimos de em.</summary>
    public double[] BoundingBox => _tables.TryGetValue("head", out var head)
        ? [Scale((short)U16(head.Offset + 36)), Scale((short)U16(head.Offset + 38)), Scale((short)U16(head.Offset + 40)), Scale((short)U16(head.Offset + 42))]
        : [0, -200, 1000, 900];

    public double Ascent => _tables.TryGetValue("hhea", out var hhea) ? Scale((short)U16(hhea.Offset + 4)) : 800;

    public double Descent => _tables.TryGetValue("hhea", out var hhea) ? Scale((short)U16(hhea.Offset + 6)) : -200;

    public double CapHeight => _tables.TryGetValue("OS/2", out var os2) && os2.Length >= 90 && U16(os2.Offset) >= 2
        ? Scale((short)U16(os2.Offset + 88))
        : Ascent * 0.7;

    /// <summary>Ângulo do itálico (tabela post), em graus.</summary>
    public double ItalicAngle => _tables.TryGetValue("post", out var post) && post.Length >= 8
        ? (short)U16(post.Offset + 4) + U16(post.Offset + 6) / 65536.0
        : 0;

    /// <summary>Nome PostScript (tabela name, ID 6), sem espaços.</summary>
    public string? PostScriptName
    {
        get
        {
            if (!_tables.TryGetValue("name", out var name)) return null;
            var count = U16(name.Offset + 2);
            var strings = name.Offset + U16(name.Offset + 4);
            for (var i = 0; i < count; i++)
            {
                var record = name.Offset + 6 + 12 * i;
                if (U16(record + 6) != 6) continue;
                var platform = U16(record);
                var length = U16(record + 8);
                var offset = strings + U16(record + 10);
                var value = platform is 0 or 3
                    ? System.Text.Encoding.BigEndianUnicode.GetString(_data, offset, length)
                    : System.Text.Encoding.Latin1.GetString(_data, offset, length);
                value = new string(value.Where(c => c is > ' ' and < '\x7F' and not ('[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '/' or '%')).ToArray());
                if (value.Length > 0) return value;
            }

            return null;
        }
    }

    private double Scale(int value) => value * 1000.0 / UnitsPerEm;

    public int GlyphCount { get; }

    /// <summary>Bits de licença de incorporação (0 = instalável).</summary>
    public int FsType { get; }

    /// <summary>A licença proíbe a incorporação (somente o bit "restrita", sem permissão de bitmap/edição).</summary>
    public bool EmbeddingRestricted => (FsType & 0x000F) == 0x0002;

    public static TrueTypeInfo? TryParse(byte[] data)
    {
        try
        {
            if (data.Length < 12) return null;
            var version = BinaryPrimitives.ReadUInt32BigEndian(data);
            if (version is not (0x00010000 or 0x74727565)) return null; // TrueType ("\0\x01\0\0" ou "true")
            return new TrueTypeInfo(data);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Largura do glifo em milésimos de em (unidades de texto do PDF).</summary>
    public double Width(int glyph)
    {
        _advances ??= ReadAdvances();
        if (_advances.Length == 0) return 0;
        var advance = glyph < _advances.Length ? _advances[glyph] : _advances[^1];
        return advance * 1000.0 / UnitsPerEm;
    }

    /// <summary>Glifo para o caractere Unicode (cmap 3,1; ou 3,0 para fontes de símbolos), 0 se não houver.</summary>
    public int Glyph(int unicode)
    {
        _cmap ??= ReadCmap();
        if (_cmap.TryGetValue(unicode, out var glyph)) return glyph;
        return _cmap.TryGetValue(0xF000 | (unicode & 0xFF), out glyph) ? glyph : 0;
    }

    public bool HasUnicodeCmap
    {
        get
        {
            _cmap ??= ReadCmap();
            return _cmap.Count > 0;
        }
    }

    /// <summary>Glifos com contorno (ou o glifo 0), pela tabela loca.</summary>
    public bool[] PresentGlyphs()
    {
        var present = new bool[GlyphCount];
        if (GlyphCount > 0) present[0] = true;
        if (!_tables.TryGetValue("loca", out var loca) || !_tables.ContainsKey("glyf")) return Enumerable.Repeat(true, GlyphCount).ToArray();
        var longFormat = U16(_tables["head"].Offset + 50) == 1;
        long Offset(int i) => longFormat ? U32(loca.Offset + 4 * i) : U16(loca.Offset + 2 * i) * 2L;
        for (var g = 0; g < GlyphCount; g++)
        {
            if ((longFormat ? 4 : 2) * (g + 1) >= loca.Length) break;
            if (Offset(g + 1) > Offset(g)) present[g] = true;
        }

        return present;
    }

    private ushort[] ReadAdvances()
    {
        if (!_tables.TryGetValue("hhea", out var hhea) || !_tables.TryGetValue("hmtx", out var hmtx)) return [];
        var count = U16(hhea.Offset + 34);
        var advances = new ushort[Math.Max((int)count, 1)];
        for (var i = 0; i < count && 4 * i + 2 <= hmtx.Length; i++) advances[i] = U16(hmtx.Offset + 4 * i);
        return advances;
    }

    private Dictionary<int, int> ReadCmap()
    {
        var map = new Dictionary<int, int>();
        if (!_tables.TryGetValue("cmap", out var cmap)) return map;
        var count = U16(cmap.Offset + 2);
        int? best = null;
        for (var i = 0; i < count; i++)
        {
            var record = cmap.Offset + 4 + 8 * i;
            var platform = U16(record);
            var encoding = U16(record + 2);
            var offset = (int)U32(record + 4);
            if (platform == 3 && encoding == 1) best = cmap.Offset + offset;
            else if (platform == 3 && encoding == 0 && best is null) best = cmap.Offset + offset;
        }

        if (best is not { } table || U16(table) != 4) return map;
        var segments = U16(table + 6) / 2;
        var ends = table + 14;
        var starts = ends + 2 * segments + 2;
        var deltas = starts + 2 * segments;
        var ranges = deltas + 2 * segments;
        for (var s = 0; s < segments; s++)
        {
            int end = U16(ends + 2 * s), start = U16(starts + 2 * s);
            var delta = (short)U16(deltas + 2 * s);
            var rangeOffset = U16(ranges + 2 * s);
            for (var c = start; c <= end && c != 0xFFFF; c++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    var address = ranges + 2 * s + rangeOffset + 2 * (c - start);
                    glyph = U16(address);
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }

                if (glyph != 0) map[c] = glyph;
            }
        }

        return map;
    }

    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset, 2));

    private uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset, 4));
}
