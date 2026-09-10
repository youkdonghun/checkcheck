using System.Windows;

namespace CheckCheck.App;

public partial class MainWindow
{
    private bool _checkingUpdate;

    private async Task CheckUpdateAsync(bool interactive)
    {
        if (_checkingUpdate || App.IsTestRun) return;
        _checkingUpdate = true;
        try
        {
            var release = await AppUpdater.CheckAsync();
            UpdateButton.Content = release is null ? "최신 버전 확인" : $"v{release.Version} 업데이트 ↗";
            if (!interactive) return;
            if (release is null) { System.Windows.MessageBox.Show(this, $"체크체크 v{AppUpdater.CurrentVersion.ToString(3)} · 최신 버전입니다.", "업데이트 확인"); return; }
            if (_busy) { SetStatus("새 버전이 있어요. 현재 작업이 끝난 뒤 업데이트를 눌러주세요."); return; }
            var answer = System.Windows.MessageBox.Show(this, $"v{release.Version} 버전을 내려받아 설치할까요?\n\n앱이 종료된 뒤 업데이트하고 다시 실행합니다. 열려 있는 글과 수정안은 사라지므로 필요한 내용을 먼저 복사해 주세요.\n\n다운로드한 로컬 AI 모델과 설정은 유지됩니다.", "체크체크 업데이트", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return;
            StartWork("업데이트 파일을 준비하고 있어요…");
            try
            {
                var download = await AppUpdater.DownloadAsync(release, ProgressReporter(), _work!.Token);
                _work.Token.ThrowIfCancellationRequested();
                AppUpdater.LaunchInstaller(download.Path, download.Hash);
                ((App)System.Windows.Application.Current).ExitForUpdate();
            }
            catch (OperationCanceledException) { SetStatus("업데이트를 취소했어요. 현재 버전을 계속 사용할 수 있어요."); }
            catch (Exception ex) { SetStatus("업데이트하지 못했어요. " + FriendlyError(ex), true); }
            finally { FinishWork(); }
        }
        catch (Exception ex) { if (interactive) SetStatus("최신 버전을 확인하지 못했어요. " + FriendlyError(ex), true); }
        finally { _checkingUpdate = false; }
    }

    private async void UpdateClick(object sender, RoutedEventArgs e) => await CheckUpdateAsync(true);
}
