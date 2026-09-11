// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// This file is part of TensorSharp.
//
// TensorSharp is licensed under the BSD-3-Clause license found in the LICENSE file in the root directory of this source tree.
//
// TensorSharp is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the BSD-3-Clause License for more details.

using System.Globalization;
using System.Text;

namespace TensorAgent.Core.Shell;

/// <summary>Line diff over a longest-common-subsequence table, rendered in `diff`'s normal and unified formats.</summary>
internal static class DiffAlgorithm
{
    internal enum EditKind { Equal, Delete, Insert }

    internal readonly record struct Edit(EditKind Kind, int AIndex, int BIndex);

    /// <summary>The edit script from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static List<Edit> Compute(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // Trim the common prefix and suffix first; the quadratic table then only
        // covers what actually changed, which for a typical edit is a few lines.
        int prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix])
            prefix++;
        int suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix])
            suffix++;

        int n = a.Count - prefix - suffix;
        int m = b.Count - prefix - suffix;
        var edits = new List<Edit>(a.Count + b.Count);
        for (int i = 0; i < prefix; i++)
            edits.Add(new Edit(EditKind.Equal, i, i));

        if ((long)n * m > 25_000_000)
        {
            // Too large for the table; report the middle as a wholesale replacement.
            for (int i = 0; i < n; i++)
                edits.Add(new Edit(EditKind.Delete, prefix + i, prefix));
            for (int j = 0; j < m; j++)
                edits.Add(new Edit(EditKind.Insert, prefix + n, prefix + j));
        }
        else
        {
            var table = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = m - 1; j >= 0; j--)
                {
                    table[i, j] = a[prefix + i] == b[prefix + j]
                        ? table[i + 1, j + 1] + 1
                        : Math.Max(table[i + 1, j], table[i, j + 1]);
                }
            }
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (a[prefix + x] == b[prefix + y])
                {
                    edits.Add(new Edit(EditKind.Equal, prefix + x, prefix + y));
                    x++;
                    y++;
                }
                else if (table[x + 1, y] >= table[x, y + 1])
                {
                    edits.Add(new Edit(EditKind.Delete, prefix + x, prefix + y));
                    x++;
                }
                else
                {
                    edits.Add(new Edit(EditKind.Insert, prefix + x, prefix + y));
                    y++;
                }
            }
            while (x < n)
            {
                edits.Add(new Edit(EditKind.Delete, prefix + x, prefix + y));
                x++;
            }
            while (y < m)
            {
                edits.Add(new Edit(EditKind.Insert, prefix + x, prefix + y));
                y++;
            }
        }

        for (int i = 0; i < suffix; i++)
            edits.Add(new Edit(EditKind.Equal, a.Count - suffix + i, b.Count - suffix + i));
        return edits;
    }

    public static string Unified(IReadOnlyList<string> a, IReadOnlyList<string> b, string aName, string bName, int context = 3)
    {
        List<Edit> edits = Compute(a, b);
        if (edits.All(e => e.Kind == EditKind.Equal))
            return string.Empty;

        var sb = new StringBuilder();
        sb.Append("--- ").Append(aName).Append('\n');
        sb.Append("+++ ").Append(bName).Append('\n');

        int i = 0;
        while (i < edits.Count)
        {
            if (edits[i].Kind == EditKind.Equal)
            {
                i++;
                continue;
            }
            // A hunk: from `context` lines before the first change to `context` lines
            // after the last change that is within 2*context of the previous one.
            int start = Math.Max(0, i - context);
            int end = i;
            int lastChange = i;
            while (end < edits.Count)
            {
                if (edits[end].Kind != EditKind.Equal)
                    lastChange = end;
                else if (end - lastChange > 2 * context)
                    break;
                end++;
            }
            end = Math.Min(edits.Count, lastChange + context + 1);

            int aStart = 0, bStart = 0, aCount = 0, bCount = 0;
            for (int k = start; k < end; k++)
            {
                if (k == start)
                {
                    aStart = edits[k].AIndex;
                    bStart = edits[k].BIndex;
                }
                switch (edits[k].Kind)
                {
                    case EditKind.Equal:
                        aCount++;
                        bCount++;
                        break;
                    case EditKind.Delete:
                        aCount++;
                        break;
                    case EditKind.Insert:
                        bCount++;
                        break;
                }
            }
            sb.Append("@@ -").Append(Range(aStart, aCount)).Append(" +").Append(Range(bStart, bCount)).Append(" @@\n");
            for (int k = start; k < end; k++)
            {
                Edit e = edits[k];
                switch (e.Kind)
                {
                    case EditKind.Equal:
                        sb.Append(' ').Append(a[e.AIndex]).Append('\n');
                        break;
                    case EditKind.Delete:
                        sb.Append('-').Append(a[e.AIndex]).Append('\n');
                        break;
                    case EditKind.Insert:
                        sb.Append('+').Append(b[e.BIndex]).Append('\n');
                        break;
                }
            }
            i = end;
        }
        return sb.ToString();
    }

    private static string Range(int start, int count)
    {
        if (count == 1)
            return (start + 1).ToString(CultureInfo.InvariantCulture);
        if (count == 0)
            return start.ToString(CultureInfo.InvariantCulture) + ",0";
        return (start + 1).ToString(CultureInfo.InvariantCulture) + "," + count.ToString(CultureInfo.InvariantCulture);
    }

    public static string Normal(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        List<Edit> edits = Compute(a, b);
        var sb = new StringBuilder();
        int i = 0;
        while (i < edits.Count)
        {
            if (edits[i].Kind == EditKind.Equal)
            {
                i++;
                continue;
            }
            int j = i;
            var deletes = new List<int>();
            var inserts = new List<int>();
            while (j < edits.Count && edits[j].Kind != EditKind.Equal)
            {
                if (edits[j].Kind == EditKind.Delete)
                    deletes.Add(edits[j].AIndex);
                else
                    inserts.Add(edits[j].BIndex);
                j++;
            }
            int aPos = deletes.Count > 0 ? deletes[0] : edits[i].AIndex;
            int bPos = inserts.Count > 0 ? inserts[0] : edits[i].BIndex;
            if (deletes.Count > 0 && inserts.Count > 0)
                sb.Append(NormalRange(deletes[0], deletes.Count)).Append('c').Append(NormalRange(inserts[0], inserts.Count)).Append('\n');
            else if (deletes.Count > 0)
                sb.Append(NormalRange(deletes[0], deletes.Count)).Append('d').Append(bPos.ToString(CultureInfo.InvariantCulture)).Append('\n');
            else
                sb.Append(aPos.ToString(CultureInfo.InvariantCulture)).Append('a').Append(NormalRange(inserts[0], inserts.Count)).Append('\n');
            foreach (int d in deletes)
                sb.Append("< ").Append(a[d]).Append('\n');
            if (deletes.Count > 0 && inserts.Count > 0)
                sb.Append("---\n");
            foreach (int ins in inserts)
                sb.Append("> ").Append(b[ins]).Append('\n');
            i = j;
        }
        return sb.ToString();
    }

    private static string NormalRange(int start, int count)
        => count == 1
            ? (start + 1).ToString(CultureInfo.InvariantCulture)
            : (start + 1).ToString(CultureInfo.InvariantCulture) + "," + (start + count).ToString(CultureInfo.InvariantCulture);
}
