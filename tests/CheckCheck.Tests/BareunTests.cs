using System.Net;
using System.Text;
using System.Text.Json;
using CheckCheck.Core;

internal static class BareunTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        const string fakeKey = "test-key-not-a-real-credential";
        void Check(bool condition, string message)
        {
            count++;
            if (!condition) throw new InvalidOperationException("Bareun fixture failed: " + message);
        }
        static string Result(string source, string revised) => JsonSerializer.Serialize(new { origin = source, revised });
        static HttpResponseMessage Respond(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        static string Preview(ReviewResult result) => new ReviewSession(result.Original, result.Suggestions).BuildPreview();

        const string source = "😀 몇일 뒤에 뵈요. teh adress";
        var fixture = JsonSerializer.Serialize(new
        {
            origin = source, revised = "😀 며칠 뒤에 봬요. teh adress",
            revisedBlocks = new[]
            {
                new { origin = new { content = "몇일", beginOffset = 3, length = 0 }, revised = "며칠",
                    revisions = new[] { new { revised = "며칠", category = "STANDARD", helpId = "days" } } }
            },
            helps = new Dictionary<string, object> { ["days"] = new { comment = "날짜를 나타내는 말은 며칠로 씁니다." } }
        });
        var handler = new FakeBareunHandler(async (request, token) =>
        {
            Check(request.Method == HttpMethod.Post, "POST method");
            Check(request.RequestUri?.AbsoluteUri == "https://api.bareun.ai/bareun.RevisionService/CorrectError", "Fixed official endpoint");
            Check(request.Headers.GetValues("api-key").Single() == fakeKey, "API key header");
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Check(payload.RootElement.GetProperty("encoding_type").GetString() == "UTF16", "UTF16 offsets requested");
            Check(payload.RootElement.GetProperty("document").GetProperty("content").GetString() == source, "Exact request source");
            return Respond(fixture);
        });
        using (var engine = new BareunProofreader(fakeKey, handler))
        {
            var result = await engine.ReviewAsync(source, ReviewMode.Minimal, null, default);
            Check(Preview(result) == "😀 며칠 뒤에 봬요. the address", "Korean cloud + local English merge");
            Check(result.Suggestions[0].Start == 3 && result.Suggestions[0].Original == "몇일", "Surrogate-safe UTF16 offset");
            Check(result.Suggestions[0].Reason.Contains("날짜") && result.Suggestions[0].Category == "표준어", "Camel case help schema");
            Check(result.Note!.Contains("영어 문법 전체"), "English limitations disclosed");
            Check(handler.Calls == 1, "Exactly one request");
        }

        foreach (var (original, revised, expected) in new[]
        {
            ("몇일 몇일", "며칠 몇일", "며칠 몇일"),
            ("오늘 15시에 몇일 일정을 확인해요.", "오늘 16시에 며칠 일정을 확인해요.", "오늘 15시에 며칠 일정을 확인해요."),
            ("😀\r\n몇일 https://example.com/abc", "😁\r\n며칠 https://example.com/changed", "😀\r\n며칠 https://example.com/abc"),
            ("오늘\r\n몇일 일정", "오늘\n몇일 일정", "오늘\r\n몇일 일정"),
            ("몇일 `teh`", "며칠 `the`", "며칠 `teh`"),
            ("몇일 Zoom미팅", "며칠 줌미팅", "며칠 Zoom미팅")
        })
        {
            using var engine = new BareunProofreader(fakeKey, new FakeBareunHandler((_, _) => Task.FromResult(Respond(Result(original, revised)))));
            var actual = Preview(await engine.ReviewAsync(original, ReviewMode.Minimal, null, default));
            Check(actual == expected, $"Repeated text, protected content, facts and identifiers: [{original}] expected [{expected}] got [{actual}]");
        }

        var offlineHandler = new FakeBareunHandler((_, _) => throw new InvalidOperationException("Network must not be called"));
        using (var engine = new BareunProofreader(fakeKey, offlineHandler))
        {
            var result = await engine.ReviewAsync("Teh adress", ReviewMode.Minimal, null, default);
            Check(Preview(result) == "The address" && result.Note!.Contains("외부 서버로 보내지 않고"), "English-only stays local");
            Check(Preview(await engine.ReviewAsync("", ReviewMode.Minimal, null, default)) == "", "Empty stays local");
            foreach (var invalid in new[] { ("한국어", ReviewMode.Natural), (new string('가', 10001), ReviewMode.Minimal) })
            {
                try { await engine.ReviewAsync(invalid.Item1, invalid.Item2, null, default); Check(false, "Invalid operation was accepted"); }
                catch (InvalidOperationException) { count++; }
            }
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await engine.ReviewAsync("한국어", ReviewMode.Minimal, null, canceled.Token); Check(false, "Canceled request was accepted"); }
            catch (OperationCanceledException) { count++; }
            Check(offlineHandler.Calls == 0, "No HTTP calls for local/invalid/canceled input");
        }

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.PaymentRequired, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
        {
            var transport = new FakeBareunHandler((_, _) => Task.FromResult(Respond("server body containing " + fakeKey, status)));
            using var engine = new BareunProofreader(fakeKey, transport);
            try { await engine.ReviewAsync("한국어", ReviewMode.Minimal, null, default); Check(false, "HTTP error was ignored"); }
            catch (InvalidOperationException e) { Check(!e.Message.Contains(fakeKey) && e.Message.Contains("바른"), "Friendly sanitized HTTP error"); }
            Check(transport.Calls == 1, "No quota/auth retry");
        }

        foreach (var invalidJson in new[] { "not JSON", "{}", "[]", Result("다른 원문", "다른 결과"), "{\"origin\":\"몇일\",\"revised\":\"\"}" })
        {
            using var engine = new BareunProofreader(fakeKey, new FakeBareunHandler((_, _) => Task.FromResult(Respond(invalidJson))));
            try { await engine.ReviewAsync("몇일", ReviewMode.Minimal, null, default); Check(false, "Invalid response was accepted"); }
            catch (InvalidOperationException e) { Check(e.Message.Contains("원문"), "Invalid schema or mismatched source rejected"); }
        }

        // Bad optional offset metadata cannot redirect a correction or poison the canonical full-document diff.
        const string malformedMetadata = "{\"origin\":\"몇일\",\"revised\":\"며칠\",\"revised_blocks\":[{\"origin\":{\"content\":\"몇일\",\"begin_offset\":\"wrong\"}}]}";
        using (var engine = new BareunProofreader(fakeKey, new FakeBareunHandler((_, _) => Task.FromResult(Respond(malformedMetadata)))))
            Check(Preview(await engine.ReviewAsync("몇일", ReviewMode.Minimal, null, default)) == "며칠", "Untrusted block offset ignored");
        foreach (var error in new Exception[] { new HttpRequestException("private diagnostic"), new OperationCanceledException() })
        {
            using var engine = new BareunProofreader(fakeKey, new FakeBareunHandler((_, _) => Task.FromException<HttpResponseMessage>(error)));
            try { await engine.ReviewAsync("몇일", ReviewMode.Minimal, null, default); Check(false, "Transport failure ignored"); }
            catch (InvalidOperationException e) { Check(!e.Message.Contains("private diagnostic"), "Transport details hidden"); }
        }
        Console.WriteLine($"PASS: {count} Bareun protocol fixture checks; no live API requests");
        return count;
    }

    private sealed class FakeBareunHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request, cancellationToken);
        }
    }
}
