using System.Windows;

namespace GenmitsuControl.Desktop;

public partial class MainWindow
{
    private void Updates_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _uiBusy || _controller?.Snapshot.Connected == true || _controller?.Snapshot.Busy == true) return;
        var dialog = new UpdateWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Handoff is null) return;
        // The helper has verified its plan and opened this process before it
        // signals ready. Normal window shutdown releases the controller first.
        // It never reconnects or replays machine commands after restarting.
        try { Close(); }
        catch { dialog.Handoff.Cancel(); throw; }
    }
}
