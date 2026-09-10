namespace CheckCheck.Core;

public enum ReviewMode { Minimal, Natural, Business }
public enum SuggestionDecision { Pending, Accepted, Skipped }

public sealed class Suggestion
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int Start { get; init; }
    public int Length { get; init; }
    public string Original { get; init; } = "";
    public string Replacement { get; init; } = "";
    public string Category { get; init; } = "교정";
    public string Reason { get; init; } = "";
    public SuggestionDecision Decision { get; set; }
}

public sealed record ReviewResult(string Original, IReadOnlyList<Suggestion> Suggestions, string Engine, string? Note = null);
public sealed record EngineProgress(string Message, double? Fraction = null);

public interface IProofreader
{
    Task<ReviewResult> ReviewAsync(string text, ReviewMode mode, IProgress<EngineProgress>? progress, CancellationToken cancellationToken);
}
