using System.Diagnostics;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using CheckCheck.Core;

var file = args.FirstOrDefault() ?? ".cache/qa/naver-long.txt";
var label = args.Skip(1).FirstOrDefault() ?? "benchmark";
var text = await File.ReadAllTextAsync(file);
var records = new List<object>();
using var runtime = new LocalModelRuntime();
using var provider = new LocalModelProofreader(runtime);
var startup = Stopwatch.StartNew();
await provider.EnsureReadyAsync(null, default);
startup.Stop();
Console.WriteLine($"READY {startup.Elapsed.TotalSeconds:F3}s {provider.DeviceDisplayName}");
if (args.Contains("--inspect"))
{
    var split = (IReadOnlyList<string>)typeof(LocalModelProofreader).GetMethod("SplitForEdits", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [text])!;
    var instructions = (string)typeof(LocalModelProofreader).GetMethod("BuildEditInstructions", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
    foreach (var chunk in split)
    {
        var watch = Stopwatch.StartNew();
        var task = (Task<IReadOnlyList<(int Start, string Original, string Replacement)>>)typeof(LocalModelRuntime).GetMethod("CompleteEditsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, [instructions, chunk, CancellationToken.None, text.Length > 240])!;
        var edits = await task;
        Console.WriteLine(JsonSerializer.Serialize(new { Seconds = watch.Elapsed.TotalSeconds, Edits = edits.Select(e => new { e.Original, e.Replacement }) }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    return;
}
var fields = typeof(LocalModelProofreader).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
for (var run = 0; run < 2; run++)
{
    // Clear only the app result cache. Keep the loaded model and llama prompt prefix warm.
    foreach (var name in new[] { "recent", "recentOrder" })
    {
        var value = fields.Single(f => f.Name == name).GetValue(provider)!;
        value.GetType().GetMethod("Clear")!.Invoke(value, null);
    }
    var timer = Stopwatch.StartNew();
    double? firstPartialSeconds = null;
    var progress = new SyncProgress(p => { if (p.PartialResult != null) firstPartialSeconds ??= timer.Elapsed.TotalSeconds; Console.WriteLine($"PROGRESS {timer.Elapsed.TotalSeconds:F3}s {p.Message}"); });
    var result = await provider.ReviewAsync(text, ReviewMode.Minimal, progress, default);
    timer.Stop();
    var revised = new ReviewSession(text, result.Suggestions).BuildPreview();
    var record = new { Run = run + 1, Characters = text.Length, Seconds = timer.Elapsed.TotalSeconds, FirstPartialSeconds = firstPartialSeconds, SuggestionCount = result.Suggestions.Count,
        result.Note, Suggestions = result.Suggestions.Select(s => new { s.Original, s.Replacement }), Revised = revised };
    records.Add(record);
    Console.WriteLine(JsonSerializer.Serialize(record, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
}
if (args.Contains("--quality"))
{
    foreach (var original in new[]
    {
        "We was late because the train were delayed.",
        "이번 보고서는 충분한 검토를 거치지 않은체 제출되었습니다.",
        "회의가 끝난뒤 자료를 정리한후 다시 연락드리겠습니다.",
        "김민수 팀장님, 오늘 18시까지 API 오류 3개는 고칠 수 없어요. https://example.com/teh 확인해 주세요."
    })
    {
        var watch = Stopwatch.StartNew();
        var result = await provider.ReviewAsync(original, ReviewMode.Minimal, null, default);
        var revised = new ReviewSession(original, result.Suggestions).BuildPreview();
        var record = new { Case = "quality", Original = original, Revised = revised, Seconds = watch.Elapsed.TotalSeconds, result.Note };
        records.Add(record);
        Console.WriteLine(JsonSerializer.Serialize(record, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    using var interrupted = new CancellationTokenSource(200);
    var watchCancel = Stopwatch.StartNew();
    try { await provider.ReviewAsync(text + "\n새 문서에는 오류가있는지 확인해 주세요.", ReviewMode.Natural, null, interrupted.Token); throw new InvalidOperationException("Active cancellation was ignored."); }
    catch (OperationCanceledException) { }
    var cancelSeconds = watchCancel.Elapsed.TotalSeconds;
    var watchNext = Stopwatch.StartNew();
    await provider.ReviewAsync("앞으로 처리할업무를 다시 정리해 주세요.", ReviewMode.Minimal, null, default);
    var cancellationRecord = new { Case = "cancellation", Seconds = cancelSeconds, NextReviewSeconds = watchNext.Elapsed.TotalSeconds };
    records.Add(cancellationRecord);
    Console.WriteLine(JsonSerializer.Serialize(cancellationRecord));
}
Directory.CreateDirectory(".cache/qa");
await File.WriteAllTextAsync($".cache/qa/{label}.json", JsonSerializer.Serialize(new { Source = file, StartupSeconds = startup.Elapsed.TotalSeconds,
    provider.DeviceDisplayName, AppResultCache = "cleared before each run", Records = records },
    new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

sealed class SyncProgress(Action<EngineProgress> action) : IProgress<EngineProgress>
{
    public void Report(EngineProgress value) => action(value);
}
