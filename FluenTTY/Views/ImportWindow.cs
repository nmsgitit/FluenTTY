using System.Windows;
using System.Windows.Controls;
using FluenTTY.Services;
using Microsoft.Win32;

namespace FluenTTY.Views;

/// <summary>Import flows shared by onboarding, the sidebar and Settings › Backup.</summary>
public static class ImportWindow
{
    /// <summary>Picks a PuTTY Connection Manager database, previews it, and imports it. Returns sessions added.</summary>
    public static int ImportPcm(Window? owner, string? path = null)
    {
        if (path == null)
        {
            var dlg = new OpenFileDialog
            {
                Title = "PuTTY Connection Manager database",
                Filter = "PuTTY CM databases (*.dat;*.xml)|*.dat;*.xml|All files|*.*",
            };
            if (dlg.ShowDialog(owner) != true) return 0;
            path = dlg.FileName;
        }

        PcmImport.Result preview;
        try { preview = PcmImport.Read(path, includePasswords: false); }
        catch (Exception ex)
        {
            FluentDialog.Info(owner, "Can't import this file", ex.Message);
            return 0;
        }
        if (preview.Sessions.Count == 0)
        {
            FluentDialog.Info(owner, "Nothing to import", "The database has no connections FluenTTY can use." +
                (preview.Warnings.Count > 0 ? "\n\n" + string.Join("\n", preview.Warnings.Take(10)) : ""));
            return 0;
        }

        var panel = new StackPanel();
        var byFolder = preview.Sessions.GroupBy(s => s.Folder).OrderBy(g => g.Key).ToList();
        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"{preview.Sessions.Count} connection{(preview.Sessions.Count == 1 ? "" : "s")} in {byFolder.Count} folder{(byFolder.Count == 1 ? "" : "s")} " +
                   $"from \"{System.IO.Path.GetFileName(path)}\". They'll appear in the Sessions sidebar under \"{byFolder[0].Key.Split('/')[0]}\".",
        };
        summary.SetResourceReference(FrameworkElement.StyleProperty, "Body");
        panel.Children.Add(summary);

        var list = new TextBox
        {
            IsReadOnly = true, MaxHeight = 200, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
            Text = string.Join("\n", byFolder.Select(g => $"{g.Key}  ({g.Count()})\n" + string.Join("\n", g.Take(8).Select(s => $"    {s.Name}  —  {Models.SessionProfile.ProtocolName(s.Protocol)} {s.DisplayTarget}")) + (g.Count() > 8 ? $"\n    … {g.Count() - 8} more" : ""))),
        };
        panel.Children.Add(list);

        var withPasswords = new CheckBox { Content = "Also import saved passwords (encrypted for your Windows account)", Margin = new Thickness(0, 12, 0, 0) };
        var replace = new CheckBox { Content = "Replace sessions that already exist with the same name and folder", Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(withPasswords);
        panel.Children.Add(replace);
        if (preview.Warnings.Count > 0)
        {
            var warn = new TextBlock { Text = string.Join("\n", preview.Warnings.Take(6)) + (preview.Warnings.Count > 6 ? $"\n… and {preview.Warnings.Count - 6} more notes" : ""), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
            warn.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
            panel.Children.Add(warn);
        }

        var dlgWin = new FluentDialog(owner, "Import from PuTTY Connection Manager", panel, "Import") { Width = 620 };
        if (dlgWin.ShowDialog() != DialogResultKind.Primary) return 0;

        var final = withPasswords.IsChecked == true ? PcmImport.Read(path, includePasswords: true) : preview;
        var (added, replaced, skipped) = PcmImport.Commit(final.Sessions, replace.IsChecked == true);
        FluentDialog.Info(owner, "Import finished",
            $"Added {added} session{(added == 1 ? "" : "s")}" +
            (replaced > 0 ? $", replaced {replaced}" : "") +
            (skipped > 0 ? $", skipped {skipped} that already existed" : "") + "." +
            (withPasswords.IsChecked == true ? $"\n{final.WithPasswords} saved password{(final.WithPasswords == 1 ? " was" : "s were")} imported." : ""));
        return added + replaced;
    }

    /// <summary>Imports PuTTY's saved sessions and, optionally, its Default Settings.</summary>
    public static void ImportPutty(Window? owner, bool sessions, bool defaults)
    {
        var lines = new List<string>();
        if (sessions)
        {
            int n = PuttyImport.ImportSessions(overwrite: false);
            lines.Add(n == 0 ? "No new PuTTY sessions to import." : $"Imported {n} session{(n == 1 ? "" : "s")} into the \"PuTTY\" folder.");
        }
        if (defaults && !PuttyImport.HasDefaultSettings())
        {
            lines.Add("PuTTY's \"Default Settings\" weren't found for your account, so nothing was changed.");
            defaults = false;
        }
        if (defaults)
        {
            var s = SettingsStore.Settings;
            var what = PuttyImport.ImportDefaultSettings(s);
            SettingsStore.NotifySettingsChanged();
            lines.Add("Imported PuTTY's default settings: " + string.Join("; ", what) + ".");
        }
        if (lines.Count > 0) FluentDialog.Info(owner, "Import from PuTTY", string.Join("\n\n", lines));
    }
}
