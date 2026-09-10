using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CheckCheck.Core;

/// <summary>Bounded token diff. All positions are UTF-16 offsets into the original string.</summary>
public static class TextDiff
{
    private sealed record Token(int Start, int Length, string Value);
    private sealed record Patch(int Start, int Length, string Replacement);
    private const long MaximumLcsCells = 1_000_000;

    public static IReadOnlyList<Suggestion> CreateSuggestions(string original, string revised, ReviewMode mode, string reason = "")
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(revised);
        if (original == revised) return Array.Empty<Suggestion>();
        var source = Tokenize(original);
        var target = Tokenize(revised);
        var patches = new List<Patch>();
        Diff(source, 0, source.Count, target, 0, target.Count, original, revised, patches);
        if (mode != ReviewMode.Minimal) patches = ExpandToSentences(original, patches);
        return patches.Select(p => new Suggestion
        {
            Start = p.Start, Length = p.Length, Original = original.Substring(p.Start, p.Length),
            Replacement = p.Replacement,
            Category = mode switch { ReviewMode.Natural => "자연스러운 표현", ReviewMode.Business => "업무용 표현", _ => "교정" },
            Reason = string.IsNullOrWhiteSpace(reason) ? "원문과 수정안의 차이를 확인해 주세요." : reason
        }).ToArray();
    }

    private static List<Token> Tokenize(string text)
    {
        var positions = StringInfo.ParseCombiningCharacters(text);
        var tokens = new List<Token>();
        var runStart = 0;
        var runKind = -1;
        for (var i = 0; i < positions.Length; i++)
        {
            var start = positions[i];
            var category = CharUnicodeInfo.GetUnicodeCategory(text, start);
            var kind = char.IsWhiteSpace(text, start) ? 0 : category is UnicodeCategory.UppercaseLetter or
                UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or
                UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.NonSpacingMark ? 1 : 2;
            // Punctuation and emoji are separate grapheme tokens, never partial surrogate pairs.
            if (runKind != -1 && (kind != runKind || kind == 2))
            {
                tokens.Add(new Token(runStart, start - runStart, text[runStart..start]));
                runStart = start;
            }
            runKind = kind;
        }
        if (positions.Length > 0) tokens.Add(new Token(runStart, text.Length - runStart, text[runStart..]));
        return tokens;
    }

    private static void Diff(List<Token> a, int aStart, int aEnd, List<Token> b, int bStart, int bEnd,
        string original, string revised, List<Patch> output)
    {
        while (aStart < aEnd && bStart < bEnd && a[aStart].Value == b[bStart].Value) { aStart++; bStart++; }
        while (aStart < aEnd && bStart < bEnd && a[aEnd - 1].Value == b[bEnd - 1].Value) { aEnd--; bEnd--; }
        if (aStart == aEnd && bStart == bEnd) return;
        var n = aEnd - aStart;
        var m = bEnd - bStart;
        if (n == 0 || m == 0)
        {
            AddPatch(a, aStart, aEnd, b, bStart, bEnd, original, revised, output);
            return;
        }
        if ((long)(n + 1) * (m + 1) > MaximumLcsCells)
        {
            // Unique word anchors keep long documents cheap; pathological repeated content remains one safe patch.
            var anchors = FindAnchors(a, aStart, aEnd, b, bStart, bEnd);
            if (anchors.Count == 0)
            {
                AddPatch(a, aStart, aEnd, b, bStart, bEnd, original, revised, output);
                return;
            }
            foreach (var (ai, bi) in anchors)
            {
                Diff(a, aStart, ai, b, bStart, bi, original, revised, output);
                aStart = ai + 1; bStart = bi + 1;
            }
            Diff(a, aStart, aEnd, b, bStart, bEnd, original, revised, output);
            return;
        }
        var width = m + 1;
        var lcs = new int[(n + 1) * width];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i * width + j] = a[aStart + i].Value == b[bStart + j].Value
                    ? lcs[(i + 1) * width + j + 1] + 1
                    : Math.Max(lcs[(i + 1) * width + j], lcs[i * width + j + 1]);
        var x = 0; var y = 0;
        var changeA = -1; var changeB = -1;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[aStart + x].Value == b[bStart + y].Value)
            {
                if (changeA >= 0)
                {
                    AddPatch(a, aStart + changeA, aStart + x, b, bStart + changeB, bStart + y, original, revised, output);
                    changeA = -1;
                }
                x++; y++;
            }
            else
            {
                if (changeA < 0) { changeA = x; changeB = y; }
                if (x < n && (y == m || lcs[(x + 1) * width + y] >= lcs[x * width + y + 1])) x++;
                else y++;
            }
        }
        if (changeA >= 0) AddPatch(a, aStart + changeA, aStart + x, b, bStart + changeB, bStart + y, original, revised, output);
    }

    private static void AddPatch(List<Token> a, int aStart, int aEnd, List<Token> b, int bStart, int bEnd,
        string original, string revised, List<Patch> output)
    {
        var start = aStart < a.Count ? a[aStart].Start : original.Length;
        var end = aEnd < a.Count ? a[aEnd].Start : original.Length;
        var newStart = bStart < b.Count ? b[bStart].Start : revised.Length;
        var newEnd = bEnd < b.Count ? b[bEnd].Start : revised.Length;
        output.Add(new Patch(start, end - start, revised.Substring(newStart, newEnd - newStart)));
    }

    private static List<(int, int)> FindAnchors(List<Token> a, int aStart, int aEnd, List<Token> b, int bStart, int bEnd)
    {
        static Dictionary<string, int> Unique(List<Token> tokens, int start, int end)
        {
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = start; i < end; i++)
                positions[tokens[i].Value] = positions.ContainsKey(tokens[i].Value) ? -1 : i;
            return positions;
        }
        var left = Unique(a, aStart, aEnd);
        var right = Unique(b, bStart, bEnd);
        var candidates = new List<(int A, int B)>();
        for (var i = aStart; i < aEnd; i++)
            if (left[a[i].Value] == i && right.TryGetValue(a[i].Value, out var bi) && bi >= 0) candidates.Add((i, bi));
        if (candidates.Count == 0) return new();
        var tails = new int[candidates.Count];
        var previous = new int[candidates.Count];
        var length = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var lo = 0; var hi = length;
            while (lo < hi) { var mid = (lo + hi) / 2; if (candidates[tails[mid]].B < candidates[i].B) lo = mid + 1; else hi = mid; }
            previous[i] = lo == 0 ? -1 : tails[lo - 1];
            tails[lo] = i;
            if (lo == length) length++;
        }
        var result = new List<(int, int)>();
        for (var i = tails[length - 1]; i >= 0; i = previous[i]) result.Add((candidates[i].A, candidates[i].B));
        result.Reverse();
        return result;
    }

    private static List<Patch> ExpandToSentences(string original, List<Patch> edits)
    {
        var boundaries = new List<int> { 0 };
        foreach (Match match in Regex.Matches(original, @"[.!?。！？](?=\s|$)|\r\n|\r|\n"))
            boundaries.Add(match.Index + match.Length);
        if (boundaries[^1] != original.Length) boundaries.Add(original.Length);
        if (original.Length == 0) return edits;
        var groups = new List<(int Start, int End, List<Patch> Edits)>();
        foreach (var edit in edits)
        {
            var first = Math.Min(edit.Start, original.Length - 1);
            var last = Math.Min(edit.Start + Math.Max(edit.Length, 1) - 1, original.Length - 1);
            var startIndex = boundaries.BinarySearch(first);
            if (startIndex < 0) startIndex = ~startIndex - 1;
            var endIndex = boundaries.BinarySearch(last);
            if (endIndex < 0) endIndex = ~endIndex - 1;
            var start = boundaries[startIndex];
            var end = boundaries[endIndex + 1];
            if (groups.Count > 0 && start < groups[^1].End)
            {
                var group = groups[^1];
                group.Edits.Add(edit);
                groups[^1] = (group.Start, Math.Max(group.End, end), group.Edits);
            }
            else groups.Add((start, end, new List<Patch> { edit }));
        }
        return groups.Select(group =>
        {
            var builder = new StringBuilder();
            var position = group.Start;
            foreach (var edit in group.Edits)
            {
                builder.Append(original, position, edit.Start - position);
                builder.Append(edit.Replacement);
                position = edit.Start + edit.Length;
            }
            builder.Append(original, position, group.End - position);
            return new Patch(group.Start, group.End - group.Start, builder.ToString());
        }).ToList();
    }
}
