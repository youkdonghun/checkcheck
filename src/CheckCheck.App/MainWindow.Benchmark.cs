using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CheckCheck.App;

public partial class MainWindow
{
    private Stopwatch? _benchmarkWatch;
    private bool _benchmarkCompleted;
    private double? _firstCheckedSeconds, _firstSuggestionSeconds;

    internal async Task RunQuickArticleBenchmarkAsync(string inputPath, string outputPath)
    {
        var text = await File.ReadAllTextAsync(inputPath);
        if (text.Length is < 1000 or > 10000) throw new InvalidDataException("Benchmark requires 1,000–10,000 characters.");
        var startup = Stopwatch.StartNew();
        await _local.EnsureReadyAsync(null, CancellationToken.None); startup.Stop();
        CheckCheck.Core.ReviewResult? completed = null;
        var quick = new QuickReviewWindow("", null, false, async (pasted, mode, progress, token) =>
        {
            if (pasted != text) throw new InvalidOperationException("The quick-window paste changed or truncated the article.");
            return completed = await ReviewQuickAsync(pasted, mode, progress, token);
        }, _native.ApplyAsync, (_, _) => { });
        quick.ShowInTaskbar = false; quick.Left = -20000; quick.Top = -20000;
        quick.WindowStartupLocation = WindowStartupLocation.Manual; quick.Show();
        try
        {
            var oldClipboard = System.Windows.Clipboard.GetDataObject();
            System.Windows.Clipboard.SetText(text);
            try { quick.PasteForBenchmark(); }
            finally
            {
                try { if (oldClipboard is not null && System.Windows.Clipboard.GetText() == text) System.Windows.Clipboard.SetDataObject(oldClipboard, true); } catch { }
            }
            await quick.RunReviewForBenchmarkAsync();
            if (completed is null) throw new InvalidOperationException("Quick review did not complete.");
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
            {
                Entry = "Right-click review window → clipboard paste → real installed AI",
                Characters = text.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
                _local.ModelDisplayName, _local.DeviceDisplayName, StartupSeconds = startup.Elapsed.TotalSeconds,
                ReviewSeconds = quick.LastReviewSeconds, FirstCheckedSeconds = quick.FirstResultSeconds,
                Suggestions = completed.Suggestions.Count, completed.Note
            }, new JsonSerializerOptions { WriteIndented = true }));
            quick.UpdateLayout();
            var surface = (FrameworkElement)quick.Content;
            var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(surface), null, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
            bitmap.Render(drawing);
            using var stream = File.Create(outputPath + ".png");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
        }
        finally { quick.Close(); }
    }

    internal async Task RunArticleBenchmarkAsync(string inputPath, string outputPath)
    {
        var text = await File.ReadAllTextAsync(inputPath);
        if (text.Length is < 1000 or > 10000) throw new InvalidDataException("Benchmark requires 1,000–10,000 characters.");
        var startup = Stopwatch.StartNew();
        await _local.EnsureReadyAsync(null, CancellationToken.None);
        startup.Stop();
        System.Windows.IDataObject? oldClipboard = null;
        try
        {
            oldClipboard = System.Windows.Clipboard.GetDataObject();
            System.Windows.Clipboard.SetText(text);
            PasteClick(this, new RoutedEventArgs());
            if (SourceEditor.Text != text) throw new InvalidOperationException("The paste route changed or truncated the benchmark article.");
        }
        finally
        {
            try
            {
                if (oldClipboard is not null && System.Windows.Clipboard.ContainsText() && System.Windows.Clipboard.GetText() == text)
                    System.Windows.Clipboard.SetDataObject(oldClipboard, true);
            }
            catch { /* A concurrent clipboard user must not prevent the review test. */ }
        }
        _firstCheckedSeconds = _firstSuggestionSeconds = null;
        _benchmarkCompleted = false;
        _benchmarkWatch = Stopwatch.StartNew();
        await ReviewAsync();
        _benchmarkWatch.Stop();
        if (!_benchmarkCompleted || _session is null) throw new InvalidOperationException("The app did not complete the review: " + StatusText.Text);
        var report = new
        {
            Entry = "WPF clipboard paste → main review → real installed local AI",
            Characters = text.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
            _local.ModelDisplayName, _local.DeviceDisplayName,
            StartupSeconds = startup.Elapsed.TotalSeconds, ReviewSeconds = _benchmarkWatch.Elapsed.TotalSeconds,
            FirstCheckedSeconds = _firstCheckedSeconds, FirstSuggestionSeconds = _firstSuggestionSeconds,
            Suggestions = _session.Suggestions.Count, Status = StatusText.Text,
            OutputCharacters = _session.BuildPreview().Length
        };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        using var stream = File.Create(outputPath + ".png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
}
