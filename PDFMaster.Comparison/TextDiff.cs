namespace PDFMaster.Comparison;

internal enum EditKind
{
    Equal,
    Delete,
    Insert,
}

/// <summary>Passo do roteiro de edição: índice em A (Equal/Delete) e em B (Equal/Insert).</summary>
internal readonly record struct Edit(EditKind Kind, int A, int B);

/// <summary>Diferença mínima entre duas sequências (algoritmo de Myers, O((N+M)·D)).</summary>
internal static class TextDiff
{
    /// <summary>Roteiro de edição, ou null quando as sequências diferem demais (mais de <paramref name="maxDistance"/> passos).</summary>
    public static List<Edit>? Diff(IReadOnlyList<string> a, IReadOnlyList<string> b, int maxDistance, CancellationToken ct = default)
    {
        int n = a.Count, m = b.Count, max = n + m;
        var edits = new List<Edit>();
        if (max == 0) return edits;

        // Prefixo e sufixo iguais saem antes (o caso mais comum: poucas mudanças no meio).
        var prefix = 0;
        while (prefix < n && prefix < m && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < n - prefix && suffix < m - prefix && a[n - 1 - suffix] == b[m - 1 - suffix]) suffix++;
        for (var i = 0; i < prefix; i++) edits.Add(new Edit(EditKind.Equal, i, i));
        var middle = Core(a, b, prefix, n - suffix, prefix, m - suffix, maxDistance, ct);
        if (middle is null) return null;
        edits.AddRange(middle);
        for (var i = suffix; i > 0; i--) edits.Add(new Edit(EditKind.Equal, n - i, m - i));
        return edits;
    }

    private static List<Edit>? Core(IReadOnlyList<string> a, IReadOnlyList<string> b, int a0, int a1, int b0, int b1, int maxDistance, CancellationToken ct)
    {
        int n = a1 - a0, m = b1 - b0, max = n + m;
        var edits = new List<Edit>();
        if (n == 0)
        {
            for (var j = b0; j < b1; j++) edits.Add(new Edit(EditKind.Insert, a0, j));
            return edits;
        }

        if (m == 0)
        {
            for (var i = a0; i < a1; i++) edits.Add(new Edit(EditKind.Delete, i, b0));
            return edits;
        }

        var offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        var limit = Math.Min(max, maxDistance);
        for (var d = 0; d <= limit; d++)
        {
            if ((d & 63) == 0) ct.ThrowIfCancellationRequested();
            var snapshot = new int[2 * d + 1];
            Array.Copy(v, offset - d, snapshot, 0, 2 * d + 1);
            trace.Add(snapshot);
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || k != d && v[offset + k - 1] < v[offset + k + 1] ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[a0 + x] == b[b0 + y])
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;
                if (x >= n && y >= m) return Backtrack(trace, n, m, a0, b0);
            }
        }

        return null;
    }

    private static List<Edit> Backtrack(List<int[]> trace, int n, int m, int a0, int b0)
    {
        static int V(int[] snapshot, int d, int k) => k < -d || k > d ? 0 : snapshot[k + d];
        var edits = new List<Edit>();
        int x = n, y = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var v = trace[d];
            var k = x - y;
            var previousK = k == -d || k != d && V(v, d, k - 1) < V(v, d, k + 1) ? k + 1 : k - 1;
            var previousX = V(v, d, previousK);
            var previousY = previousX - previousK;
            while (x > previousX && y > previousY)
            {
                edits.Add(new Edit(EditKind.Equal, a0 + x - 1, b0 + y - 1));
                x--;
                y--;
            }

            if (d > 0)
            {
                if (x == previousX) edits.Add(new Edit(EditKind.Insert, a0 + x, b0 + y - 1));
                else edits.Add(new Edit(EditKind.Delete, a0 + x - 1, b0 + y));
            }

            x = previousX;
            y = previousY;
        }

        edits.Reverse();
        return edits;
    }
}
