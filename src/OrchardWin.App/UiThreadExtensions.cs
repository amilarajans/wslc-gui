using Microsoft.UI.Dispatching;

namespace OrchardWin.App;

/// Marshals service-raised events onto the UI thread before they touch controls.
///
/// Why this must exist: several Core services raise change notifications from thread-pool
/// threads - StatsService's sampler runs on a System.Threading.Timer, ModelServerService's
/// crash detection fires from Process.Exited, ImageService clears pull progress from a
/// Task.Delay continuation, and SystemService refreshes properties via Task.Run. Orchard's
/// Swift originals never had this problem (@MainActor serialized everything); WinUI throws
/// RPC_E_WRONG_THREAD the moment a DependencyProperty is touched off the dispatcher thread.
/// Every page/dialog/tray subscription that ends in UI work routes through here.
internal static class UiThreadExtensions
{
    public static void RunOnUi(this DispatcherQueue queue, Action action)
    {
        if (queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            queue.TryEnqueue(() => action());
        }
    }
}
