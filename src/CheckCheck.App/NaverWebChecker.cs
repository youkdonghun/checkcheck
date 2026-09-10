using System.Diagnostics;

namespace CheckCheck.App;

internal static class NaverWebChecker
{
    internal const string Url = "https://search.naver.com/search.naver?query=%EB%A7%9E%EC%B6%A4%EB%B2%95%20%EA%B2%80%EC%82%AC%EA%B8%B0";
    internal static string Open(string text)
    {
        // Text is copied locally, never embedded in the search URL or sent through an undocumented API.
        if (!string.IsNullOrWhiteSpace(text)) System.Windows.Clipboard.SetText(text);
        Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
        return string.IsNullOrWhiteSpace(text) ? "네이버 웹 검사기를 열었어요." : "원문을 복사했어요. 열린 네이버 검사기에 Ctrl+V로 붙여넣으세요.";
    }
}
