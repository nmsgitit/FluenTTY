using FluenTTY.Models;
using FluenTTY.Views;

namespace FluenTTY.Services;

/// <summary>Opens tabs in the main window from anywhere (file browsers opening editors, the start page, pop-outs).</summary>
public static class TabService
{
    /// <summary>Set by MainWindow.</summary>
    public static Action<SessionTab, bool>? OpenTabHandler { get; set; }
    public static Action<SessionProfile>? OpenSessionHandler { get; set; }

    public static void Open(SessionTab tab, bool activate = true) => OpenTabHandler?.Invoke(tab, activate);

    public static void OpenSession(SessionProfile profile) => OpenSessionHandler?.Invoke(profile);
}
