using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluenTTY.Connections;

namespace FluenTTY.Views;

/// <summary>PuTTY's host key alert: first contact ("not cached") or a changed key ("WARNING - POTENTIAL SECURITY BREACH").</summary>
public static class HostKeyDialog
{
    /// <returns>true = accept and remember, null = connect once, false = cancel.</returns>
    public static bool? Show(Window? owner, HostKeyInfo info)
    {
        var panel = new StackPanel();

        string intro = info.PreviousAlgorithm != null
            ? $"FluenTTY has a {info.PreviousAlgorithm} host key on record for {info.Host} (port {info.Port}), but the server presented a key of another type ({info.Algorithm}) instead. Servers normally keep offering the key you already accepted, so this can mean someone is impersonating the server (a man-in-the-middle attack)."
            : info.Mismatch
            ? $"The host key for {info.Host} (port {info.Port}) does not match the one FluenTTY has on record. Either the server's key was changed by its administrator, or someone is impersonating the server (a man-in-the-middle attack)."
            : $"FluenTTY has no host key on record for {info.Host} (port {info.Port}). There is no guarantee the server is the computer you think it is.";
        var introText = new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap };
        introText.SetResourceReference(FrameworkElement.StyleProperty, "Body");
        panel.Children.Add(introText);

        if (info.Mismatch)
        {
            var warn = new Border { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(4) };
            warn.SetResourceReference(Border.BackgroundProperty, "SystemFillColorCriticalBackgroundBrush");
            var wt = new TextBlock
            {
                Text = info.PreviousAlgorithm != null
                    ? "Only continue if you know the server's administrator added this key type. The key on record is kept as well."
                    : "Only continue if you know the server's key was changed. The old key will be replaced.",
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold,
            };
            wt.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            warn.Child = wt;
            panel.Children.Add(warn);
        }

        var details = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Margin = new Thickness(0, 14, 0, 0),
            Text = $"Key type:       {info.Algorithm} ({info.KeyBits} bits)\n" +
                   $"SHA256:         {info.FingerprintSha256.Replace("SHA256:", "")}\n" +
                   $"MD5:            {info.FingerprintMd5}" +
                   (info.PreviousFingerprint != null ? $"\nPreviously:     {info.PreviousFingerprint.Replace("SHA256:", "")}" : ""),
        };
        panel.Children.Add(details);

        var hint = new TextBlock
        {
            Text = "Accept stores the key so you won't be asked again. Connect once trusts it for this tab only.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        };
        hint.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
        panel.Children.Add(hint);

        var dlg = new FluentDialog(owner, info.Mismatch ? "Host key has changed" : "Verify the server's host key", panel,
            info.Mismatch ? "Accept new key" : "Accept", "Connect once", "Cancel", destructive: info.Mismatch)
        {
            Width = 560,
        };
        // A changed key must never be accepted by a stray Enter (the dialog appears mid-connect, often
        // while the user is typing): Cancel is the default and the accept buttons arm after a moment.
        // Security review M3 — see CLAUDE.md "Security invariants".
        if (info.Mismatch) dlg.MakeCancelDefault(armAfter: TimeSpan.FromSeconds(1.5));
        return dlg.ShowDialog() switch
        {
            DialogResultKind.Primary => true,
            DialogResultKind.Secondary => null,
            _ => false,
        };
    }
}
