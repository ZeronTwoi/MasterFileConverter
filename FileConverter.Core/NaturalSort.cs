using FileConverter.Core.Platform;

namespace FileConverter.Core;

/// <summary>
/// File-manager-style "natural" ordering: digit runs compare numerically, so
/// 1, 2, 10 and img_2 before img_10 — regardless of the naming convention.
/// The platform supplies the comparison its own file manager uses where there is one.
/// </summary>
public sealed class NaturalSort : IComparer<string?>
{
    public static readonly NaturalSort Instance = new();

    public int Compare(string? a, string? b)
    {
        if (a == null || b == null) return string.CompareOrdinal(a, b);
        return PlatformServices.Current.CompareFileNames(a, b);
    }

    /// <summary>Orders full paths by their file name (then by path for stability).</summary>
    public static List<string> SortPathsByFileName(IEnumerable<string> paths) =>
        paths.OrderBy(Path.GetFileName, Instance).ThenBy(p => p, PlatformServices.PathComparer).ToList();

    public static int CompareManaged(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length) return na.Length - nb.Length;
                int numCmp = na.CompareTo(nb, StringComparison.Ordinal);
                if (numCmp != 0) return numCmp;
            }
            else
            {
                int cmp = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (cmp != 0) return cmp;
                i++; j++;
            }
        }
        return (a.Length - i) - (b.Length - j);
    }
}
