namespace RankMaster2.Pc.Ui.Surface;

/// <summary>
/// Folder names in the order a person expects (plan I § 2.2 item 3): digit runs compare as numbers,
/// everything else case-insensitively, so <c>Day 9</c> comes before <c>Day 10</c>. A port of the
/// phone's <c>ui/review/NaturalOrder.kt</c>, with the same tie rules: names that look the same
/// (<c>a</c> / <c>A</c>, <c>a1</c> / <c>a01</c>) are ordered by the raw ordinal, so the result is stable.
/// </summary>
public sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    public int Compare(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        var i = 0;
        var j = 0;
        while (i < a.Length && j < b.Length)
        {
            var ca = a[i];
            var cb = b[j];
            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                var endA = RunEnd(a, i);
                var endB = RunEnd(b, j);
                var numA = a[i..endA].TrimStart('0');
                var numB = b[j..endB].TrimStart('0');
                if (numA.Length != numB.Length) return numA.Length - numB.Length;
                var byDigits = string.CompareOrdinal(numA, numB);
                if (byDigits != 0) return byDigits;
                i = endA;
                j = endB;
            }
            else
            {
                var byChar = char.ToLowerInvariant(ca).CompareTo(char.ToLowerInvariant(cb));
                if (byChar != 0) return byChar;
                i++;
                j++;
            }
        }

        var byLength = (a.Length - i) - (b.Length - j);
        if (byLength != 0) return byLength;
        return string.CompareOrdinal(a, b);
    }

    private static int RunEnd(string s, int from)
    {
        var k = from;
        while (k < s.Length && char.IsDigit(s[k])) k++;
        return k;
    }
}
