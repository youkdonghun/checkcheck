using System.Text;

namespace CheckCheck.Core;

/// <summary>A review always applies original-coordinate patches to an unchanged source.</summary>
public sealed class ReviewSession
{
    private readonly List<Suggestion> _suggestions;
    private readonly Stack<SuggestionDecision[]> _history = new();

    public string Original { get; }
    public IReadOnlyList<Suggestion> Suggestions { get; }
    public bool CanUndo => _history.Count > 0;

    public ReviewSession(string original, IReadOnlyList<Suggestion> suggestions)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(suggestions);
        Original = original;
        _suggestions = suggestions.OrderBy(s => s.Start).ThenBy(s => s.Length).Select(s => new Suggestion
        {
            Id = s.Id, Start = s.Start, Length = s.Length, Original = s.Original,
            Replacement = s.Replacement, Category = s.Category, Reason = s.Reason, Decision = s.Decision
        }).ToList();
        Suggestion? previous = null;
        foreach (var suggestion in _suggestions)
        {
            if (suggestion.Start < 0 || suggestion.Length < 0 || suggestion.Start > original.Length - suggestion.Length)
                throw new ArgumentException("교정 범위가 원문을 벗어났습니다.", nameof(suggestions));
            if (!original.AsSpan(suggestion.Start, suggestion.Length).SequenceEqual(suggestion.Original.AsSpan()))
                throw new ArgumentException("교정 대상과 원문이 일치하지 않습니다.", nameof(suggestions));
            if (SplitsSurrogate(original, suggestion.Start) || SplitsSurrogate(original, suggestion.Start + suggestion.Length))
                throw new ArgumentException("교정 범위가 유니코드 문자를 나눌 수 없습니다.", nameof(suggestions));
            if (previous is not null && (suggestion.Start < previous.Start + previous.Length || suggestion.Start == previous.Start))
                throw new ArgumentException("교정 제안의 범위가 겹칩니다.", nameof(suggestions));
            if (!Enum.IsDefined(suggestion.Decision)) throw new ArgumentException("알 수 없는 검토 상태입니다.", nameof(suggestions));
            previous = suggestion;
        }
        Suggestions = _suggestions.AsReadOnly();
    }

    public string BuildPreview(bool includePending = true) => Build(s => s.Decision == SuggestionDecision.Accepted ||
        includePending && s.Decision == SuggestionDecision.Pending);

    public string BuildAccepted() => BuildPreview(false);

    public void SetDecision(int index, SuggestionDecision decision)
    {
        if (!Enum.IsDefined(decision)) throw new ArgumentOutOfRangeException(nameof(decision));
        if ((uint)index >= (uint)_suggestions.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (_suggestions[index].Decision == decision) return;
        SaveHistory();
        _suggestions[index].Decision = decision;
    }

    public void AcceptAll()
    {
        if (_suggestions.All(s => s.Decision == SuggestionDecision.Accepted)) return;
        SaveHistory();
        foreach (var suggestion in _suggestions) suggestion.Decision = SuggestionDecision.Accepted;
    }

    public bool Undo()
    {
        if (!_history.TryPop(out var decisions)) return false;
        for (var i = 0; i < decisions.Length; i++) _suggestions[i].Decision = decisions[i];
        return true;
    }

    private void SaveHistory() => _history.Push(_suggestions.Select(s => s.Decision).ToArray());

    private string Build(Func<Suggestion, bool> include)
    {
        var builder = new StringBuilder(Original.Length);
        var position = 0;
        foreach (var suggestion in _suggestions.Where(include))
        {
            builder.Append(Original, position, suggestion.Start - position);
            builder.Append(suggestion.Replacement);
            position = suggestion.Start + suggestion.Length;
        }
        builder.Append(Original, position, Original.Length - position);
        return builder.ToString();
    }

    private static bool SplitsSurrogate(string text, int position) => position > 0 && position < text.Length &&
        char.IsHighSurrogate(text[position - 1]) && char.IsLowSurrogate(text[position]);
}
