using System.Text.RegularExpressions;

namespace CheckCheck.Core;

/// <summary>Small, deliberately conservative offline rules. This is not a general grammar model.</summary>
public sealed class RuleProofreader : IProofreader
{
    private sealed record Rule(Regex Pattern, Func<Match, string> Replace, string Category, string Reason);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex ProtectedText = new(@"```[\s\S]*?(?:```|$)|`[^`\r\n]*`|(?:https?://|www\.)\S+|[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|[A-Za-z]:\\[^\r\n\t]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Rule[] BasicRules = BuildBasicRules();
    private static readonly Rule[] NaturalRules =
    [
        Fixed(@"(?<![가-힣])관련하여(?=\s)", "관련해", "표현", "자주 쓰는 표현을 조금 더 간결하게 바꿉니다."),
        Fixed(@"에 대하여(?=\s)", "에 대해", "표현", "긴 표현을 짧게 다듬습니다."),
        Fixed(@"\bin order to\b", "to", "표현", "목적을 나타내는 표현을 간결하게 다듬습니다.", true),
        Fixed(@"\bat this point in time\b", "now", "표현", "긴 시간 표현을 간결하게 다듬습니다.", true)
    ];
    private static readonly Rule[] BusinessRules =
    [
        Fixed(@"확인했어요(?=$|[\s.!?~])", "확인했습니다", "업무용 표현", "확인한 사실을 유지하면서 업무용 말투로 바꿉니다."),
        Fixed(@"답변[ ]?주세요(?=$|[\s.!?~])", "답변 부탁드립니다", "업무용 표현", "답변 요청을 정중한 업무용 표현으로 바꿉니다."),
        Fixed(@"확인[ ]?부탁해요(?=$|[\s.!?~])", "확인 부탁드립니다", "업무용 표현", "확인 요청을 정중한 업무용 표현으로 바꿉니다."),
        Fixed(@"확인해[ ]?주세요(?=$|[\s.!?~])", "확인 부탁드립니다", "업무용 표현", "확인 요청을 정중한 업무용 표현으로 바꿉니다."),
        Fixed(@"(?<![가-힣])고마워요(?=$|[\s.!?~])", "감사합니다", "업무용 표현", "감사 표현을 업무용 말투로 바꿉니다."),
        Fixed(@"(?<![가-힣])감사해요(?=$|[\s.!?~])", "감사합니다", "업무용 표현", "감사 표현을 업무용 말투로 바꿉니다."),
        Fixed(@"(?<![가-힣])미안해요(?=$|[\s.!?~])", "죄송합니다", "업무용 표현", "원문에 있는 사과 표현을 정중하게 바꿉니다."),
        Fixed(@"\bCan you please\b", "Could you please", "업무용 표현", "요청을 조금 더 정중한 표현으로 바꿉니다.", true),
        Fixed(@"\bThanks a lot\b", "Thank you", "업무용 표현", "감사 표현을 업무용 말투로 바꿉니다.", true)
    ];

    public Task<ReviewResult> ReviewAsync(string text, ReviewMode mode, IProgress<EngineProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Task.Run(() => Review(text, mode, progress, cancellationToken), cancellationToken);
    }

    private static ReviewResult Review(string text, ReviewMode mode, IProgress<EngineProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        progress?.Report(new EngineProgress("기기에 저장된 규칙으로 검사하고 있어요.", 0.2));
        var suggestions = FindSuggestions(text, BasicRules, token);
        if (mode == ReviewMode.Minimal)
        {
            progress?.Report(new EngineProgress("기본 검사를 마쳤어요.", 1));
            return new ReviewResult(text, suggestions, "오프라인 기본 규칙",
                "기본 규칙은 등록된 한·영 오탈자와 일부 띄어쓰기·문법만 확인합니다. 제안이 없어도 오류가 없다는 뜻은 아닙니다.");
        }
        var corrected = new ReviewSession(text, suggestions).BuildPreview();
        var styleSuggestions = FindSuggestions(corrected, mode == ReviewMode.Business ? BusinessRules : NaturalRules, token);
        var revised = new ReviewSession(corrected, styleSuggestions).BuildPreview();
        token.ThrowIfCancellationRequested();
        var atomic = TextDiff.CreateSuggestions(text, revised, mode,
            "등록된 기본 교정과 표현 규칙을 적용한 문장입니다. 의미와 말투가 맞는지 확인해 주세요.");
        progress?.Report(new EngineProgress("기본 검사를 마쳤어요.", 1));
        return new ReviewResult(text, atomic, "오프라인 기본 규칙",
            "기본 규칙 모드의 말투 변경은 몇 가지 자주 쓰는 표현으로 한정됩니다. 문맥을 이해하는 전체 문장 재작성은 로컬 AI 모드를 이용해 주세요.");
    }

    private static IReadOnlyList<Suggestion> FindSuggestions(string text, IEnumerable<Rule> rules, CancellationToken token)
    {
        var protectedRanges = ProtectedText.Matches(text).Select(m => (Start: m.Index, End: m.Index + m.Length)).ToArray();
        var result = new List<Suggestion>();
        foreach (var rule in rules)
        {
            token.ThrowIfCancellationRequested();
            foreach (Match match in rule.Pattern.Matches(text))
            {
                token.ThrowIfCancellationRequested();
                var end = match.Index + match.Length;
                if (protectedRanges.Any(p => match.Index < p.End && end > p.Start)) continue;
                if (result.Any(s => match.Index < s.Start + s.Length && end > s.Start)) continue;
                var replacement = rule.Replace(match);
                if (replacement == match.Value) continue;
                result.Add(new Suggestion
                {
                    Start = match.Index, Length = match.Length, Original = match.Value,
                    Replacement = replacement, Category = rule.Category, Reason = rule.Reason
                });
            }
        }
        return result.OrderBy(s => s.Start).ToArray();
    }

    private static Rule[] BuildBasicRules()
    {
        var rules = new List<Rule>
        {
            Fixed(@"안[ ]?됬", "안 됐", "맞춤법", "‘되었’의 준말은 ‘됐’이며, 부정 부사 ‘안’은 띄어 씁니다."),
            Fixed(@"안[ ]?되요(?=$|[\s.!?~,])", "안 돼요", "맞춤법", "‘되어요’의 준말은 ‘돼요’이며, ‘안’은 띄어 씁니다."),
            Fixed(@"됬", "됐", "맞춤법", "‘되었’의 준말은 ‘됐’입니다."),
            Fixed(@"햇(?=어요|습니다|네요|죠|다(?:$|[\s.!?~,]))", "했", "맞춤법", "‘하였’의 준말은 ‘했’입니다."),
            Fixed(@"(?<![가-힣])되요(?=$|[\s.!?~,])", "돼요", "맞춤법", "‘되어요’의 준말은 ‘돼요’입니다."),
            Fixed(@"(?<![가-힣])뵈요(?=$|[\s.!?~,])", "봬요", "맞춤법", "‘뵈어요’를 줄여 쓰면 ‘봬요’입니다."),
            Fixed(@"어떻해(?=$|[\s.!?~,])", "어떡해", "맞춤법", "‘어떻게 해’의 준말은 ‘어떡해’입니다."),
            Fixed(@"몇일", "며칠", "맞춤법", "날짜나 기간을 뜻할 때는 ‘며칠’로 씁니다."),
            Fixed(@"웬지(?=$|[\s.!?~,])", "왠지", "맞춤법", "‘왜인지’의 준말은 ‘왠지’입니다."),
            Fixed(@"금새(?=\s|$)", "금세", "맞춤법", "‘지금 바로’라는 뜻은 ‘금세’로 씁니다."),
            Fixed(@"오랫만", "오랜만", "맞춤법", "‘오래간만’의 준말은 ‘오랜만’입니다."),
            Fixed(@"역활", "역할", "맞춤법", "맡은 일을 뜻하는 말은 ‘역할’입니다."),
            Fixed(@"설겆이", "설거지", "맞춤법", "그릇을 씻는 일은 ‘설거지’로 씁니다."),
            Fixed(@"할께(?=$|[요\s.!?~,])", "할게", "맞춤법", "의지나 약속을 나타내는 어미는 ‘-ㄹ게’로 씁니다."),
            Fixed(@"부탁 드(?=[립릴리])", "부탁드", "띄어쓰기", "‘부탁드리다’는 붙여 씁니다."),
            new Rule(new Regex(@"(?<verb>할|될|볼|갈|올|쓸|알|먹을|읽을|받을|보낼|드릴)[ ]*수[ ]*(?<ending>있|없)", RegexOptions.Compiled, RegexTimeout),
                m => m.Groups["verb"].Value + " 수 " + m.Groups["ending"].Value, "띄어쓰기", "의존 명사 ‘수’는 앞뒤 단어와 띄어 씁니다."),
            Fixed(@"\bI is\b", "I am", "영문 문법", "주어 ‘I’에는 현재형 ‘am’을 씁니다.", true),
            Fixed(@"\bI has\b", "I have", "영문 문법", "주어 ‘I’에는 ‘have’를 씁니다.", true),
            new Rule(new Regex(@"\b(?<subject>he|she|it) are\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout),
                m => m.Groups["subject"].Value + " is", "영문 문법", "단수 주어 he/she/it에는 ‘is’를 씁니다."),
            Fixed(@"\byou was\b", "you were", "영문 문법", "주어 ‘you’에는 과거형 ‘were’를 씁니다.", true),
            new Rule(new Regex(@"\ba (?<noun>apple|orange|egg|umbrella|idea|example|hour)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout),
                m => MatchCase(m.Value[..1], "an") + " " + m.Groups["noun"].Value, "영문 문법", "모음 소리로 시작하는 이 단어 앞에는 ‘an’을 씁니다."),
            Fixed(@"\ban university\b", "a university", "영문 문법", "‘university’는 자음 소리로 시작하므로 ‘a’를 씁니다.", true),
            Fixed(@"\bthe[ \t]+the\b", "the", "영문 중복", "연속해서 중복된 ‘the’를 확인해 주세요.", true),
            new Rule(new Regex(@"\bi(?= (?:am|have|will|think|would|can|was)\b)", RegexOptions.Compiled, RegexTimeout),
                _ => "I", "대소문자", "영어의 1인칭 대명사 ‘I’는 대문자로 씁니다.")
        };
        foreach (var (wrong, correct) in new[]
        {
            ("teh", "the"), ("recieve", "receive"), ("recieved", "received"), ("seperate", "separate"),
            ("definately", "definitely"), ("occured", "occurred"), ("occurence", "occurrence"),
            ("adress", "address"), ("grammer", "grammar"), ("tommorow", "tomorrow"),
            ("becuase", "because"), ("wierd", "weird"), ("alot", "a lot"),
            ("dont", "don't"), ("doesnt", "doesn't"), ("isnt", "isn't"), ("didnt", "didn't")
        }) rules.Add(Fixed(@"\b" + Regex.Escape(wrong) + @"\b", correct, "영문 오탈자", $"‘{wrong}’의 일반적인 표기는 ‘{correct}’입니다.", true));
        return rules.ToArray();
    }

    private static Rule Fixed(string pattern, string replacement, string category, string reason, bool ignoreCase = false) =>
        new(new Regex(pattern, RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), RegexTimeout),
            match => ignoreCase ? MatchCase(match.Value, replacement) : replacement, category, reason);

    private static string MatchCase(string original, string replacement)
    {
        var letters = original.Where(char.IsLetter).ToArray();
        if (letters.Length > 1 && letters.All(char.IsUpper)) return replacement.ToUpperInvariant();
        if (original.Length > 0 && char.IsUpper(original[0]) && replacement.Length > 0)
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        return replacement;
    }
}
