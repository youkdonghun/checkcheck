using CheckCheck.Core;

if (args.Contains("--local-model"))
{
    await LocalModelEvaluation.RunAsync();
    return;
}

var count = 0;
void Check(bool condition, string message)
{
    count++;
    if (!condition) throw new InvalidOperationException("FAILED: " + message);
}
void Equal(string expected, string actual, string message) => Check(expected == actual, $"{message}\nExpected: {expected}\nActual: {actual}");
void Reject(Action action, string message)
{
    try { action(); }
    catch (ArgumentException) { count++; return; }
    throw new InvalidOperationException("FAILED: " + message);
}

var cases = new (string Original, string Revised)[]
{
    ("", ""), ("", "반가워요 👋"), ("전부 삭제", ""),
    ("the cat and the cat", "the dog and the cat"),
    ("앞 😀 뒤", "앞 👨‍👩‍👧‍👦 다음"),
    ("e\u0301 cafe", "é café"),
    ("첫째\r\n둘째\r\n셋째", "첫째\r\n두번째\r\n셋째!"),
    ("a a a a a", "a a b a a"),
    ("안녕 하세요. 고마워요!", "안녕하세요. 감사합니다!"),
    ("A. B. C.", "First A. Better B. C. Added."),
    ("a\r\nb\n", "a\nb\r\n"),
    ("hello", "hello!"), ("hello world", "hello wonderful world"),
    ("A. B.", "A.B."), ("A.\nB.", "AB."),
    ("✈️ okay 🇰🇷 test", "✈️ fine 🇺🇸 test"),
};
foreach (var (original, revised) in cases)
foreach (var mode in Enum.GetValues<ReviewMode>())
{
    var suggestions = TextDiff.CreateSuggestions(original, revised, mode);
    var session = new ReviewSession(original, suggestions);
    Equal(original, session.BuildAccepted(), "Pending edits do not alter accepted output");
    Equal(revised, session.BuildPreview(), $"Diff reconstructs {mode}");
    session.AcceptAll();
    Equal(revised, session.BuildAccepted(), "Accept all reconstructs target");
    Equal(original, session.Original, "Source stays unchanged");
}

// Deterministic fuzzing exercises repeated anchors, deletions, insertions, line endings, and UTF-16 text.
var random = new Random(173);
string[] atoms = ["가", "나", "다", " ", "\r\n", "\n", "a", "a", "b", ". ", "😀", "👩‍💻", "e\u0301"];
for (var iteration = 0; iteration < 500; iteration++)
{
    var source = Enumerable.Range(0, random.Next(1, 45)).Select(_ => atoms[random.Next(atoms.Length)]).ToList();
    var target = source.ToList();
    for (var mutation = 0; mutation < 5; mutation++)
    {
        var position = random.Next(target.Count + 1);
        if (position < target.Count && random.Next(2) == 0) target.RemoveAt(position);
        else target.Insert(position, atoms[random.Next(atoms.Length)]);
    }
    var original = string.Concat(source);
    var revised = string.Concat(target);
    foreach (var mode in Enum.GetValues<ReviewMode>())
    {
        var session = new ReviewSession(original, TextDiff.CreateSuggestions(original, revised, mode));
        Equal(revised, session.BuildPreview(), $"Fuzz {iteration}, {mode}");
    }
}

var longOriginal = string.Join(" ", Enumerable.Range(0, 1800).Select(i => "word" + i));
var longRevised = longOriginal.Replace("word10 ", "changed10 ").Replace("word1700 ", "changed1700 ");
var longSuggestions = TextDiff.CreateSuggestions(longOriginal, longRevised, ReviewMode.Minimal);
Equal(longRevised, new ReviewSession(longOriginal, longSuggestions).BuildPreview(), "Long diff anchor path reconstructs");
Check(longSuggestions.Count == 2, "Long document retains two independent typo patches");
var repeatedOriginal = string.Concat(Enumerable.Repeat("a ", 1400));
var repeatedRevised = "b " + repeatedOriginal[2..^2] + "c ";
Equal(repeatedRevised, new ReviewSession(repeatedOriginal, TextDiff.CreateSuggestions(repeatedOriginal, repeatedRevised, ReviewMode.Minimal)).BuildPreview(),
    "Pathological repeated document has bounded safe fallback");

var review = new ReviewSession("wrong and bad", new[]
{
    new Suggestion { Start = 10, Length = 3, Original = "bad", Replacement = "good" },
    new Suggestion { Start = 0, Length = 5, Original = "wrong", Replacement = "right" }
});
review.SetDecision(0, SuggestionDecision.Accepted);
review.SetDecision(1, SuggestionDecision.Skipped);
Equal("right and bad", review.BuildAccepted(), "Selection applies only accepted patches by original position");
Equal("right and bad", review.BuildPreview(), "Skipped suggestions remain original");
Check(review.Undo(), "Undo exists");
Equal("right and good", review.BuildPreview(), "Undo restores skipped suggestion to pending");
review.AcceptAll();
Equal("right and good", review.BuildAccepted(), "Accept all works after mixed decisions");
Check(review.Undo(), "Accept all can be undone as one action");
Equal("right and bad", review.BuildAccepted(), "Undo all restores former accepted decisions");
Check(review.Undo(), "Single decision undo exists");
Equal("wrong and bad", review.BuildAccepted(), "Undo preserves original");
Check(!review.Undo(), "Empty history returns false");
Reject(() => new ReviewSession("abc", [new Suggestion { Start = 0, Length = 2, Original = "XX", Replacement = "a" }]), "Reject stale source");
Reject(() => new ReviewSession("abc", [new Suggestion { Start = 2, Length = 5, Original = "c", Replacement = "a" }]), "Reject invalid ranges");
Reject(() => new ReviewSession("abc", [new Suggestion { Start = 0, Length = 2, Original = "ab" }, new Suggestion { Start = 1, Length = 2, Original = "bc" }]), "Reject overlaps");
Reject(() => new ReviewSession("abc", [new Suggestion { Start = 1, Length = 0, Original = "", Replacement = "x" }, new Suggestion { Start = 1, Length = 0, Original = "", Replacement = "y" }]), "Reject ambiguous same-position insertions");
Reject(() => new ReviewSession("😀", [new Suggestion { Start = 0, Length = 1, Original = "\ud83d" }]), "Reject split surrogate");

var rules = new RuleProofreader();
async Task<string> Correct(string text, ReviewMode mode = ReviewMode.Minimal)
{
    var result = await rules.ReviewAsync(text, mode, null, CancellationToken.None);
    Check(!string.IsNullOrWhiteSpace(result.Note), "Rule limitations are disclosed");
    return new ReviewSession(text, result.Suggestions).BuildPreview();
}
Equal("안 돼요. 며칠 뒤에 봬요. 할 수 있어요.", await Correct("안되요. 몇일 뒤에 뵈요. 할수있어요."), "Korean explicit spelling and spacing");
Equal("확인했어요. 확인했습니다. 확인했네요. 확인했죠. 확인했다. 햇살이 좋아요.",
    await Correct("확인햇어요. 확인햇습니다. 확인햇네요. 확인햇죠. 확인햇다. 햇살이 좋아요."), "Known haet endings corrected while haetsal is preserved");
Equal("I am here. She is ready. The address is separate.", await Correct("I is here. She are ready. Teh adress is seperate."), "English rules preserve capitalization");
Equal("오늘 15시까지 보고서 3부 확인 부탁드립니다. 감사합니다.",
    await Correct("오늘 15시까지 보고서 3부 확인 부탁해요. 고마워요.", ReviewMode.Business), "Business expressions preserve facts and deadlines");
Equal("오늘 18시까지 답변 부탁드립니다. 확인했습니다. 햇살이 좋아요.",
    await Correct("오늘 18시까지 답변 주세요. 확인햇어요. 햇살이 좋아요.", ReviewMode.Business), "Business sample preserves deadline and sunlight noun");
Equal("이 문제에 대해 검토합니다. I called to ask.",
    await Correct("이 문제에 대하여 검토합니다. I called in order to ask.", ReviewMode.Natural), "Natural mode uses explicit concise expression rules");
var clean = "내일 오후 3시까지 보고서를 보내 주세요. 감사합니다. I have a university degree. 😀";
Equal(clean, await Correct(clean), "Correct common text is not changed");
var protectedText = "https://example.com/teh/adress `teh adress` name@recieve.com ```\nteh = adress\n```";
Equal(protectedText, await Correct(protectedText), "URLs, email, inline and fenced code are protected");
var repeated = "teh cat and teh dog";
Equal("the cat and the dog", await Correct(repeated), "Repeated spelling edits retain exact offsets");
var canceled = new CancellationTokenSource();
canceled.Cancel();
try { await rules.ReviewAsync("teh", ReviewMode.Minimal, null, canceled.Token); throw new Exception("Cancellation was ignored"); }
catch (OperationCanceledException) { count++; }
count += await BareunTests.RunAsync();
Console.WriteLine($"PASS: {count} checks (diff, Unicode, review decisions, safety, offline rules)");
