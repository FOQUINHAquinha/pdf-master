namespace PDFMaster.OCR;

/// <summary>
/// Imagem em tons de cinza (8 bits) usada na preparação para o OCR: contraste, ruído, rotação e
/// detecção de inclinação. Todas as operações devolvem uma nova imagem.
/// </summary>
public sealed class GrayImage
{
    public GrayImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length < (long)width * height)
            throw new ArgumentException("Dimensões de imagem inválidas.");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public static GrayImage FromBgra(int width, int height, byte[] bgra)
    {
        var gray = new byte[width * height];
        for (int i = 0, j = 0; i < gray.Length; i++, j += 4)
        {
            gray[i] = (byte)((bgra[j + 2] * 77 + bgra[j + 1] * 150 + bgra[j] * 29) >> 8);
        }

        return new GrayImage(width, height, gray);
    }

    /// <summary>Gira em 90, 180 ou 270 graus no sentido horário.</summary>
    public GrayImage Rotate90(int clockwiseDegrees)
    {
        var k = ((clockwiseDegrees % 360) + 360) % 360;
        if (k == 0) return this;
        if (k is not (90 or 180 or 270)) throw new ArgumentException("A rotação deve ser múltipla de 90 graus.");

        if (k == 180)
        {
            var flipped = new byte[Width * Height];
            for (var i = 0; i < flipped.Length; i++) flipped[i] = Pixels[flipped.Length - 1 - i];
            return new GrayImage(Width, Height, flipped);
        }

        var result = new byte[Width * Height];
        var newWidth = Height;
        var newHeight = Width;
        for (var v = 0; v < newHeight; v++)
        {
            for (var u = 0; u < newWidth; u++)
            {
                // 90°: destino(u,v) = origem(v, H-1-u).  270°: destino(u,v) = origem(W-1-v, u).
                var (x, y) = k == 90 ? (v, Height - 1 - u) : (Width - 1 - v, u);
                result[v * newWidth + u] = Pixels[y * Width + x];
            }
        }

        return new GrayImage(newWidth, newHeight, result);
    }

    /// <summary>
    /// Gira em um ângulo qualquer em torno do centro, mantendo o tamanho e preenchendo com branco.
    /// Definição: destino(p) = origem(c + R(a)·(p − c)), com R(a) = [cos a, −sen a; sen a, cos a].
    /// </summary>
    public GrayImage Rotate(double degrees)
    {
        if (Math.Abs(degrees) < 1e-6) return this;
        var a = degrees * Math.PI / 180;
        var cos = Math.Cos(a);
        var sin = Math.Sin(a);
        var cx = Width / 2.0;
        var cy = Height / 2.0;
        var result = new byte[Width * Height];

        for (var q = 0; q < Height; q++)
        {
            var dy = q - cy;
            for (var p = 0; p < Width; p++)
            {
                var dx = p - cx;
                var sx = cx + cos * dx - sin * dy;
                var sy = cy + sin * dx + cos * dy;
                var x0 = (int)Math.Floor(sx);
                var y0 = (int)Math.Floor(sy);
                if (x0 < 0 || y0 < 0 || x0 >= Width - 1 || y0 >= Height - 1)
                {
                    result[q * Width + p] = 255;
                    continue;
                }

                var fx = sx - x0;
                var fy = sy - y0;
                var i = y0 * Width + x0;
                var top = Pixels[i] * (1 - fx) + Pixels[i + 1] * fx;
                var bottom = Pixels[i + Width] * (1 - fx) + Pixels[i + Width + 1] * fx;
                result[q * Width + p] = (byte)(top * (1 - fy) + bottom * fy + 0.5);
            }
        }

        return new GrayImage(Width, Height, result);
    }

    /// <summary>Estica o histograma entre os percentis 1 e 99 (melhora digitalizações desbotadas).</summary>
    public GrayImage StretchContrast()
    {
        var histogram = new int[256];
        foreach (var value in Pixels.AsSpan(0, Width * Height)) histogram[value]++;

        var total = Width * Height;
        var cut = total / 100;
        int low = 0, high = 255, sum = 0;
        while (low < 255 && (sum += histogram[low]) <= cut) low++;
        sum = 0;
        while (high > 0 && (sum += histogram[high]) <= cut) high--;
        if (high - low < 16 || (low <= 4 && high >= 251)) return this;

        var table = new byte[256];
        for (var i = 0; i < 256; i++) table[i] = (byte)Math.Clamp((i - low) * 255 / (high - low), 0, 255);
        var result = new byte[total];
        for (var i = 0; i < total; i++) result[i] = table[Pixels[i]];
        return new GrayImage(Width, Height, result);
    }

    /// <summary>Filtro de mediana 3×3: remove pontos isolados ("sal e pimenta").</summary>
    public GrayImage Median3()
    {
        if (Width < 3 || Height < 3) return this;
        var result = (byte[])Pixels.Clone();
        Span<byte> window = stackalloc byte[9];
        for (var y = 1; y < Height - 1; y++)
        {
            for (var x = 1; x < Width - 1; x++)
            {
                var n = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var row = (y + dy) * Width + x;
                    window[n++] = Pixels[row - 1];
                    window[n++] = Pixels[row];
                    window[n++] = Pixels[row + 1];
                }

                window.Sort();
                result[y * Width + x] = window[4];
            }
        }

        return new GrayImage(Width, Height, result);
    }

    /// <summary>
    /// Estima a inclinação do texto. Devolve o ângulo, em graus, que passado a <see cref="Rotate"/>
    /// deixa as linhas na horizontal; 0 quando não há inclinação mensurável (até ±7°).
    /// </summary>
    public double EstimateSkew()
    {
        // Trabalha em uma amostra reduzida: a precisão necessária é de décimos de grau.
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(Width, Height) / 1000.0));
        var w = Width / step;
        var h = Height / step;
        if (w < 40 || h < 40) return 0;

        var sample = new byte[w * h];
        var histogram = new int[256];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var value = Pixels[y * step * Width + x * step];
                sample[y * w + x] = value;
                histogram[value]++;
            }
        }

        var threshold = Otsu(histogram, w * h);
        var xs = new List<int>();
        var ys = new List<int>();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (sample[y * w + x] < threshold)
                {
                    xs.Add(x);
                    ys.Add(y);
                }
            }
        }

        // Página em branco, ou escura demais para ser texto sobre papel.
        if (xs.Count < 200 || xs.Count > w * h * 0.6) return 0;

        var cx = w / 2.0;
        var cy = h / 2.0;
        var rows = new int[h * 3];

        double Score(double degrees)
        {
            Array.Clear(rows);
            var a = degrees * Math.PI / 180;
            var cos = Math.Cos(a);
            var sin = Math.Sin(a);
            for (var i = 0; i < xs.Count; i++)
            {
                // Linha em que o ponto cai depois de Rotate(degrees).
                var q = (int)Math.Round(cy - sin * (xs[i] - cx) + cos * (ys[i] - cy)) + h;
                if (q >= 0 && q < rows.Length) rows[q]++;
            }

            double score = 0;
            foreach (var count in rows) score += (double)count * count;
            return score;
        }

        var baseline = Score(0);
        var best = 0.0;
        var bestScore = baseline;
        for (var angle = -7.0; angle <= 7.0; angle += 0.5)
        {
            var score = Score(angle);
            if (score > bestScore) (best, bestScore) = (angle, score);
        }

        var coarse = best;
        for (var angle = coarse - 0.4; angle <= coarse + 0.4; angle += 0.1)
        {
            var score = Score(angle);
            if (score > bestScore) (best, bestScore) = (angle, score);
        }

        // Só corrige quando o ganho é claro; evita "corrigir" páginas que já estão retas.
        return Math.Abs(best) >= 0.25 && bestScore > baseline * 1.05 ? Math.Round(best, 1) : 0;
    }

    private static int Otsu(int[] histogram, int total)
    {
        double sumAll = 0;
        for (var i = 0; i < 256; i++) sumAll += i * (double)histogram[i];

        double sumBack = 0, best = -1;
        long weightBack = 0;
        var threshold = 128;
        for (var i = 0; i < 256; i++)
        {
            weightBack += histogram[i];
            if (weightBack == 0) continue;
            var weightFore = total - weightBack;
            if (weightFore == 0) break;
            sumBack += i * (double)histogram[i];
            var meanBack = sumBack / weightBack;
            var meanFore = (sumAll - sumBack) / weightFore;
            var between = (double)weightBack * weightFore * (meanBack - meanFore) * (meanBack - meanFore);
            if (between > best)
            {
                best = between;
                threshold = i + 1;
            }
        }

        return threshold;
    }

    /// <summary>Codifica como BMP de 8 bits (formato que o Tesseract lê diretamente da memória).</summary>
    public byte[] ToBmp(int dpi)
    {
        var rowSize = (Width + 3) & ~3;
        var dataSize = rowSize * Height;
        const int headerSize = 14 + 40 + 1024;
        var file = new byte[headerSize + dataSize];
        using (var writer = new BinaryWriter(new MemoryStream(file)))
        {
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(file.Length);
            writer.Write(0);
            writer.Write(headerSize);

            writer.Write(40);
            writer.Write(Width);
            writer.Write(Height);
            writer.Write((short)1);
            writer.Write((short)8);
            writer.Write(0);
            writer.Write(dataSize);
            var pixelsPerMeter = (int)Math.Round(dpi / 0.0254);
            writer.Write(pixelsPerMeter);
            writer.Write(pixelsPerMeter);
            writer.Write(256);
            writer.Write(0);

            for (var i = 0; i < 256; i++)
            {
                writer.Write((byte)i);
                writer.Write((byte)i);
                writer.Write((byte)i);
                writer.Write((byte)0);
            }
        }

        // As linhas do BMP são gravadas de baixo para cima.
        for (var y = 0; y < Height; y++)
        {
            Buffer.BlockCopy(Pixels, y * Width, file, headerSize + (Height - 1 - y) * rowSize, Width);
        }

        return file;
    }
}
