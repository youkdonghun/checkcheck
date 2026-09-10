using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using CheckCheck.Core;

// Opt-in integration evaluation. This downloads the pinned model and runs entirely on this PC.
internal static class LocalModelEvaluation
{
    public static async Task RunAsync()
    {
        using var engine = new LocalModelProofreader();
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        var last = Stopwatch.StartNew();
        var progress = new Progress<EngineProgress>(p =>
        {
            if (p.Fraction == null || p.Fraction == 1 || last.Elapsed.TotalSeconds >= 10)
            { Console.WriteLine(p.Message); last.Restart(); }
        });
        var startup = Stopwatch.StartNew();
        await engine.EnsureReadyAsync(progress, cancel.Token);
        Console.WriteLine($"Ready: {engine.DeviceDisplayName}, {startup.Elapsed.TotalSeconds:F1}s");
        var cases = new (string Name, ReviewMode Mode, string Original)[]
        {
            ("Korean spelling", ReviewMode.Minimal, "회의에 참석하지 못할것 같아요. 몇일 뒤에 다시 연락드릴께요."),
            ("Korean spacing", ReviewMode.Minimal, "자료를 검토한후 문제가있는지 알려주세요."),
            ("English grammar", ReviewMode.Minimal, "She don't have the informations. I has recieved your mesage yesterday."),
            ("Korean business", ReviewMode.Business, "내일 오후 3시까지 자료 좀 보내줘. 확인하고 연락할게."),
            ("English business", ReviewMode.Business, "Send me the report by Friday. I need it for the meeting."),
            ("Natural Korean", ReviewMode.Natural, "이 부분에 대해서는 제가 생각하기에는 조금 더 검토를 하는 것이 필요할 것 같습니다."),
            ("Clean Korean", ReviewMode.Minimal, "내일 오후 3시까지 보고서를 보내 주세요. 감사합니다."),
            ("Preserve facts", ReviewMode.Business, "김민수 팀장님, 오늘 18시까지 API 오류 3개는 고칠 수 없어요. https://example.com/teh 확인해 주세요.")
        };
        var records = new List<object>();
        foreach (var item in cases)
        {
            var timer = Stopwatch.StartNew();
            var result = await engine.ReviewAsync(item.Original, item.Mode, progress, cancel.Token);
            var revised = new ReviewSession(item.Original, result.Suggestions).BuildPreview();
            var record = new { item.Name, Mode = item.Mode.ToString(), item.Original, Revised = revised, Suggestions = result.Suggestions.Count,
                Seconds = Math.Round(timer.Elapsed.TotalSeconds, 2), result.Note };
            records.Add(record);
            Console.WriteLine(JsonSerializer.Serialize(record, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        Directory.CreateDirectory(".cache/qa");
        await File.WriteAllTextAsync(".cache/qa/local-model-evaluation.json", JsonSerializer.Serialize(new { engine.ModelDisplayName, engine.DeviceDisplayName, Cases = records },
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), cancel.Token);
    }
}
