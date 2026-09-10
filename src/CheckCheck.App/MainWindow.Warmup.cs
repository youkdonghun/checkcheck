using System.Windows;
using CheckCheck.Core;

namespace CheckCheck.App;

public partial class MainWindow
{
    private readonly CancellationTokenSource _warmupCancel = new();
    private bool _warming;

    private async Task WarmInstalledAiAsync()
    {
        if (_warming || _local.IsReady || !_local.IsInstalled || App.IsTestRun) return;
        _warming = true;
        try
        {
            await _local.EnsureReadyAsync(new Progress<EngineProgress>(p =>
            {
                if (LocalEngine.IsChecked == true) EngineStatus.Text = "AI 미리 불러오는 중…";
            }), _warmupCancel.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_warmupCancel.IsCancellationRequested) SetStatus("AI를 미리 불러오지 못했어요. 검사할 때 다시 시도합니다. " + FriendlyError(ex), true); }
        finally { _warming = false; if (!_warmupCancel.IsCancellationRequested) UpdateEngineStatus(); }
    }

}
