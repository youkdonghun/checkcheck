using System.Reflection;
using System.Text.Json;
using CheckCheck.Core;

internal static class LocalEditProtocolTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string message)
        {
            count++;
            if (!condition) throw new InvalidOperationException("FAILED: " + message);
        }
        (string Revised, bool Rejected) Apply(string text, params (string Original, string Replacement)[] edits)
        {
            object?[] args = [text, edits.Select(e => (text.IndexOf(e.Original, StringComparison.Ordinal), e.Original, e.Replacement)).ToArray(), false];
            var revised = (string)typeof(LocalModelProofreader).GetMethod("ApplyEdits", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
            return (revised, (bool)args[2]!);
        }
        var safe = Apply("첫째 문장에 문제가있는지 확인해 주세요.\r\nHe go to school every day.", ("문제가있는지", "문제가 있는지"), ("He go", "He goes"));
        Check(safe is { Rejected: false, Revised: "첫째 문장에 문제가 있는지 확인해 주세요.\r\nHe goes to school every day." }, "Sparse Korean/English edits retain exact source and CRLF");
        var unchanged = Apply("원문입니다. 😀");
        Check(unchanged is { Rejected: false, Revised: "원문입니다. 😀" }, "Empty edits preserve the complete input");
        foreach (var edits in new (string, string)[][]
        {
            [("missing", "new")], [("", "insert")], [("text", "fixed"), ("text", "other")],
            [("123", "124")], [("today", "tomorrow")],
            [("can't", "can")], [("API", "接口")], [("😀", "🙂")],
            [("text same", "fixed same"), ("same 123", "same 1234")]
        })
        {
            const string source = "text same 123 today can't API 😀 same";
            var result = Apply(source, edits);
            Check(result.Rejected && result.Revised == source, "Unsafe, missing, duplicate, or overlapping anchors must never be guessed");
        }
        Check(Apply("aa", ("aa", "aa")) is { Rejected: false, Revised: "aa" }, "Unchanged model spans do not create edits");
        Check(Apply("Read the the report.", ("the the report", "the report")) is { Rejected: false, Revised: "Read the report." }, "Deletion keeps enough non-empty context for protected-token validation");
        Check(Apply("😀 okay", ("\ud83d", "x")).Rejected, "Sparse spans may not split surrogate pairs");
        var sourceText = string.Concat(Enumerable.Repeat("첫째 문장입니다.\r\nSecond sentence. 😀 ", 170));
        var split = (IReadOnlyList<string>)typeof(LocalModelProofreader).GetMethod("SplitForEdits", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [sourceText])!;
        Check(string.Concat(split) == sourceText, "Long sparse chunks reconstruct the input exactly");
        Check(split.All(s => s.Length is > 0 and <= 1100), "Sparse chunks remain bounded");
        Check(split.All(s => !char.IsHighSurrogate(s[^1]) && !char.IsLowSurrogate(s[0]) && s[^1] != '\r'), "Chunks preserve Unicode and CRLF boundaries");
        var sentences = (IReadOnlyList<(int Start, string Text)>)typeof(LocalModelRuntime).GetMethod("SplitSentences", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["  첫 문장입니다.\r\n3.14와 `a. b`를 보세요. 마지막입니다!"])!;
        Check(sentences.Count == 3 && sentences[1].Text == "3.14와 `a. b`를 보세요.", "Decimals and punctuation in code do not create sentence boundaries");
        Check(sentences[0].Start == 2 && sentences[0].Text == "첫 문장입니다.", "Sentence IDs retain exact source offsets while leaving whitespace untouched");
        foreach (var payload in new[]
        {
            "{\"edits\":[{\"id\":-1,\"revised\":\"x\"}]}",
            "{\"edits\":[{\"id\":3,\"revised\":\"x\"}]}",
            "{\"edits\":[{\"id\":0.5,\"revised\":\"x\"}]}",
            "{\"edits\":[{\"id\":\"0\",\"revised\":\"x\"}]}",
            "{\"edits\":[{\"id\":0,\"revised\":\"x\"},{\"id\":0,\"revised\":\"y\"}]}"
        })
        {
            using var json = JsonDocument.Parse(payload);
            var rejected = false;
            try { typeof(LocalModelRuntime).GetMethod("DecodeSentenceEdits", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [json.RootElement, sentences]); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { rejected = true; }
            Check(rejected, "Unknown, fractional, non-number, and duplicate sentence IDs fail clearly");
        }
        using var repeatedIds = JsonDocument.Parse("{\"edits\":[{\"id\":1,\"revised\":\"He goes.\"}]}");
        var repeatedSentences = new (int Start, string Text)[] { (0, "He go."), (7, "He go.") };
        var repeatedEdits = (IReadOnlyList<(int Start, string Original, string Replacement)>)typeof(LocalModelRuntime).GetMethod("DecodeSentenceEdits", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [repeatedIds.RootElement, repeatedSentences])!;
        object?[] repeatedArgs = ["He go. He go.", repeatedEdits, false];
        var repeatedRevised = (string)typeof(LocalModelProofreader).GetMethod("ApplyEdits", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, repeatedArgs)!;
        Check(repeatedRevised == "He go. He goes." && !(bool)repeatedArgs[2]!, "Repeated sentences are corrected by explicit ID without guessing an occurrence");
        foreach (var ids in new[] { "[-1]", "[3]", "[0.5]", "[\"0\"]", "[0,0]" })
        {
            using var json = JsonDocument.Parse("{\"incorrect_ids\":" + ids + "}");
            var rejected = false;
            try { typeof(LocalModelRuntime).GetMethod("DecodeIncorrectSentenceIds", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [json.RootElement, 3]); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { rejected = true; }
            Check(rejected, "Detection IDs receive the same strict validation as correction IDs");
        }
        return count;
    }
}
