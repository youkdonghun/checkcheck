using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CheckCheck.Core;

/// <summary>
/// Optional, explicitly selected Korean cloud correction. No paid-provider fallback or retry.
/// Protocol: https://bareun.ai/docs/howtouse/rest-api/ and /howtouse/api-correct/.
/// </summary>
public sealed class BareunProofreader : IProofreader, IDisposable
{
    private static readonly Uri Endpoint = new("https://api.bareun.ai/bareun.RevisionService/CorrectError");
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex Hangul = new(@"[\u1100-\u11FF\u3130-\u318F\uAC00-\uD7AF]", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex ProtectedText = new(@"```[\s\S]*?(?:```|$)|`[^`\r\n]*`|(?:https?://|www\.)\S+|[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|[A-Za-z]:\\[^\r\n\t]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex Facts = new(@"\d+(?:[.,:/-]\d+)*|[A-Za-z][A-Za-z0-9_-]*", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex LineBreaks = new(@"\r\n|\r|\n", RegexOptions.Compiled, RegexTimeout);
    private readonly HttpClient _client;
    private readonly RuleProofreader _rules = new();
    private sealed record Explanation(int Start, int Length, string Category, string Reason);

    public BareunProofreader(string apiKey) : this(apiKey, new HttpClientHandler { AllowAutoRedirect = false }) { }

    // Injectable transport permits protocol/error tests without sending text or keys to a service.
    public BareunProofreader(string apiKey, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 1024 || apiKey.Any(char.IsControl))
            throw new ArgumentException("설정에서 바른 API 키를 입력해 주세요.", nameof(apiKey));
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
        _client.DefaultRequestHeaders.Add("api-key", apiKey.Trim());
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<ReviewResult> ReviewAsync(string text, ReviewMode mode, IProgress<EngineProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (mode != ReviewMode.Minimal)
            throw new InvalidOperationException("바른 API는 ‘최소 교정’에서 사용해 주세요. 자연스럽게·업무용 말투는 로컬 AI 모드에서 사용할 수 있습니다.");
        if (text.Length > 10_000)
            throw new InvalidOperationException("바른 API는 한 번에 10,000자까지 검사합니다. 검사할 부분을 나누어 선택해 주세요.");
        if (!Hangul.IsMatch(text))
        {
            var local = await _rules.ReviewAsync(text, mode, progress, cancellationToken).ConfigureAwait(false);
            return local with { Note = "한국어가 없어 외부 서버로 보내지 않고 영어 기본 규칙으로 검사했습니다. 영어 문법 전체를 검사하는 기능은 아닙니다." };
        }

        progress?.Report(new EngineProgress("바른 서버에서 한국어 맞춤법을 검사하고 있어요.", 0.2));
        // UTF16 matches C# / WPF offsets. Disable optional cleanup and style-like spacing changes.
        var payload = JsonSerializer.Serialize(new
        {
            document = new { content = text, language = "ko-KR" }, encoding_type = "UTF16",
            config = new { disable_vx_spacing = true, enable_cleanup_whitespace = false, enable_sentence_check = true }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw HttpError(response.StatusCode);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryString(root, "origin", out var origin) || origin != text ||
                !TryString(root, "revised", out var revised) || string.IsNullOrWhiteSpace(revised))
                throw InvalidResponse();
            if (revised.Length > Math.Max(20_000, text.Length * 3)) throw InvalidResponse();
            cancellationToken.ThrowIfCancellationRequested();

            // Use the full revised document as the canonical output. Nested blocks can overlap, and
            // examples in the official docs have zero lengths; never trust those as writable ranges.
            var explanations = ReadExplanations(root, text);
            var protectedRanges = ProtectedRanges(text);
            var suggestions = new List<Suggestion>();
            var filtered = 0;
            foreach (var change in TextDiff.CreateSuggestions(text, revised, ReviewMode.Minimal))
            {
                if (protectedRanges.Any(p => Overlaps(change.Start, change.Length, p.Start, p.End - p.Start)) ||
                    !Facts.Matches(change.Original).Select(m => m.Value).SequenceEqual(Facts.Matches(change.Replacement).Select(m => m.Value)) ||
                    !LineBreaks.Matches(change.Original).Select(m => m.Value).SequenceEqual(LineBreaks.Matches(change.Replacement).Select(m => m.Value)))
                { filtered++; continue; }
                var explanation = explanations.Where(e => Overlaps(change.Start, change.Length, e.Start, e.Length))
                    .OrderBy(e => e.Length).FirstOrDefault();
                suggestions.Add(new Suggestion
                {
                    Start = change.Start, Length = change.Length, Original = change.Original, Replacement = change.Replacement,
                    Category = explanation?.Category ?? "한국어 교정",
                    Reason = explanation?.Reason ?? "바른 API가 제안한 한국어 교정입니다. 원래 의도에 맞는지 확인해 주세요."
                });
            }
            // Keep English capability explicit and local. Both engines use the unchanged source,
            // so mixing suggestions cannot shift offsets or apply the same range twice.
            var basic = await _rules.ReviewAsync(text, ReviewMode.Minimal, null, cancellationToken).ConfigureAwait(false);
            foreach (var english in basic.Suggestions.Where(s => !Hangul.IsMatch(s.Original) && !Hangul.IsMatch(s.Replacement)))
                if (!suggestions.Any(s => Overlaps(s.Start, s.Length, english.Start, english.Length))) suggestions.Add(english);
            var validated = new ReviewSession(text, suggestions).Suggestions;
            progress?.Report(new EngineProgress("한국어 교정 결과를 받았어요.", 1));
            var note = "한국어는 바른 API, 영어는 기기의 기본 규칙으로 검사했습니다. 영어 문법 전체와 말투 재작성은 지원하지 않습니다. API 사용량은 연결한 바른 계정의 요금제에 따릅니다.";
            if (filtered > 0) note += $" URL·코드·숫자·영문 표기·이모지·줄바꿈을 바꾸는 제안 {filtered}개는 보존을 위해 제외했습니다.";
            return new ReviewResult(text, validated, "바른 API · 영어 기본 규칙", note);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("바른 서버 응답 시간이 초과됐습니다. 잠시 후 다시 검사해 주세요. 자동 재시도는 하지 않았습니다.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("바른 서버에 연결하지 못했습니다. 인터넷 연결을 확인하거나 오프라인 기본 규칙을 선택해 주세요.");
        }
        catch (JsonException) { throw InvalidResponse(); }
    }

    private static InvalidOperationException HttpError(HttpStatusCode status) => new(status switch
    {
        HttpStatusCode.Unauthorized => "바른 API 키를 확인할 수 없습니다. 설정에서 키를 다시 확인해 주세요.",
        HttpStatusCode.Forbidden => "이 바른 API 키로 맞춤법 검사를 사용할 수 없습니다. 바른 계정의 키·서비스 권한을 확인해 주세요.",
        HttpStatusCode.PaymentRequired or HttpStatusCode.TooManyRequests => "바른 API 사용 한도에 도달했거나 요청이 많습니다. 바른 계정의 무료 사용량을 확인하거나 오프라인 기본 규칙을 선택해 주세요. 자동 재시도나 유료 전환은 하지 않았습니다.",
        HttpStatusCode.RequestEntityTooLarge => "바른 서버에서 글이 너무 길다고 응답했습니다. 검사할 부분을 나누어 선택해 주세요.",
        HttpStatusCode.BadRequest => "바른 서버가 검사 요청을 처리하지 못했습니다. 입력 내용이나 API 키의 서비스 설정을 확인해 주세요.",
        _ when (int)status >= 500 => "바른 서버에 일시적인 문제가 있습니다. 잠시 후 다시 검사하거나 오프라인 기본 규칙을 선택해 주세요.",
        _ => $"바른 API 요청을 완료하지 못했습니다(HTTP {(int)status}). 자동 재시도는 하지 않았습니다."
    });

    private static InvalidOperationException InvalidResponse() => new("바른 서버 응답이 원문과 맞지 않거나 교정 결과를 읽을 수 없습니다. 원문은 변경하지 않았습니다.");

    private static List<Explanation> ReadExplanations(JsonElement root, string text)
    {
        var results = new List<Explanation>();
        if (!TryProperty(root, "revised_blocks", "revisedBlocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array) return results;
        root.TryGetProperty("helps", out var helps);
        foreach (var block in blocks.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("origin", out var span) || span.ValueKind != JsonValueKind.Object ||
                !TryString(span, "content", out var content)) continue;
            var start = 0; // Protobuf JSON can omit the zero-valued beginOffset field.
            if (TryProperty(span, "begin_offset", "beginOffset", out var offset) && (offset.ValueKind != JsonValueKind.Number || !offset.TryGetInt32(out start))) continue;
            if (start < 0 || start > text.Length - content.Length || !text.AsSpan(start, content.Length).SequenceEqual(content.AsSpan())) continue;
            var category = "한국어 교정";
            var reason = "바른 API가 제안한 한국어 교정입니다.";
            if (block.TryGetProperty("revisions", out var revisions) && revisions.ValueKind == JsonValueKind.Array && revisions.GetArrayLength() > 0)
            {
                var revision = revisions[0];
                if (revision.ValueKind != JsonValueKind.Object) continue;
                if (TryString(revision, "category", out var kind)) category = CategoryName(kind);
                if (TryProperty(revision, "help_id", "helpId", out var helpId) && helpId.ValueKind == JsonValueKind.String &&
                    helps.ValueKind == JsonValueKind.Object && helps.TryGetProperty(helpId.GetString()!, out var help) &&
                    TryString(help, "comment", out var comment) && !string.IsNullOrWhiteSpace(comment))
                    reason = comment.Length > 600 ? comment[..600] : comment;
            }
            results.Add(new Explanation(start, content.Length, category, reason));
        }
        return results;
    }

    private static string CategoryName(string category) => category switch
    {
        "GRAMMER" or "WORD" => "한국어 문법", "SPACING" => "띄어쓰기", "TYPO" => "오탈자",
        "STANDARD" => "표준어", "FOREIGN_WORD" => "외래어 표기", "CONFUSABLE_WORDS" => "단어 확인",
        "SENTENCE" => "문장 교정", "CONFIRM" => "확인 필요", _ => "한국어 교정"
    };

    private static List<(int Start, int End)> ProtectedRanges(string text)
    {
        var ranges = ProtectedText.Matches(text).Select(m => (Start: m.Index, End: m.Index + m.Length)).ToList();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (CharUnicodeInfo.GetUnicodeCategory(element, 0) == UnicodeCategory.OtherSymbol)
                ranges.Add((elements.ElementIndex, elements.ElementIndex + element.Length));
        }
        return ranges;
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()!;
        return true;
    }

    private static bool TryProperty(JsonElement element, string snake, string camel, out JsonElement property) =>
        element.TryGetProperty(snake, out property) || element.TryGetProperty(camel, out property);

    private static bool Overlaps(int start, int length, int otherStart, int otherLength) =>
        start == otherStart || length == 0 && start > otherStart && start < otherStart + otherLength ||
        otherLength == 0 && otherStart > start && otherStart < start + length ||
        start < otherStart + otherLength && otherStart < start + length;

    public void Dispose() => _client.Dispose();
}
