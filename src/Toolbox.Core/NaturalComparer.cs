using System.Globalization;

namespace Toolbox.Core;

/// <summary>
/// Orders "Page 2" before "Page 10". Digit runs (ASCII and Arabic-Indic) compare numerically;
/// everything else compares with the current culture, so Arabic names sort as users expect.
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (IsDigit(x[i]) && IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && IsDigit(x[i])) i++;
                while (j < y.Length && IsDigit(y[j])) j++;
                int cmp = CompareNumbers(x.AsSpan(si, i - si), y.AsSpan(sj, j - sj));
                if (cmp != 0) return cmp;
            }
            else
            {
                int si = i, sj = j;
                while (i < x.Length && !IsDigit(x[i])) i++;
                while (j < y.Length && !IsDigit(y[j])) j++;
                int cmp = string.Compare(x.Substring(si, i - si), y.Substring(sj, j - sj),
                    CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
                if (cmp != 0) return cmp;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }

    private static bool IsDigit(char c) => char.IsDigit(c);

    private static int CompareNumbers(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        a = TrimZeros(a);
        b = TrimZeros(b);
        if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
        for (int k = 0; k < a.Length; k++)
        {
            int da = (int)char.GetNumericValue(a[k]), db = (int)char.GetNumericValue(b[k]);
            if (da != db) return da.CompareTo(db);
        }
        return 0;
    }

    private static ReadOnlySpan<char> TrimZeros(ReadOnlySpan<char> s)
    {
        int k = 0;
        while (k < s.Length - 1 && char.GetNumericValue(s[k]) == 0) k++;
        return s[k..];
    }
}
