// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.IO;
using System.Windows;
using Microsoft.Win32;
using NDM.App.Localization;

namespace NDM.App;

public partial class SettingsWindow
{
    /// <summary>The engine's queues for backup and restore (null in tests without an engine: the file then carries no queues).</summary>
    public IQueueBackup? Queues { get; set; }
    /// <summary>Set after a restore: the caller opens Options again so every page shows the restored values.</summary>
    public bool ReopenRequested { get; private set; }
    /// <summary>Tests answer the two file questions without a file dialog.</summary>
    public static Func<bool, string?>? BackupFilePicker { get; set; }

    private static string? PickBackupFile(bool save, Window owner)
    {
        if (BackupFilePicker is { } pick) return pick(save);
        if (Dialogs.TestMode) return null;
        var filter = Loc.T("options.backup.file_filter");
        if (save)
        {
            var d = new SaveFileDialog { Filter = filter, FileName = "NeoDM-backup-" + DateTime.Now.ToString("yyyyMMdd") + ".json", DefaultExt = ".json", OverwritePrompt = true };
            return d.ShowDialog(owner) == true ? d.FileName : null;
        }
        var o = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        return o.ShowDialog(owner) == true ? o.FileName : null;
    }

    private void BackupExport_Click(object sender, RoutedEventArgs e)
    {
        BackupState.Text = "";
        if (PickBackupFile(true, this) is not { Length: > 0 } path) return;
        try
        {
            var text = SettingsBackup.Export(_settings, Queues?.List(), Queues?.DefaultId);
            var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try { File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false)); File.Move(temp, path, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            BackupState.Text = Loc.T("options.backup.saved", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { BackupState.Text = Loc.T("options.backup.save_failed"); }
    }

    private void BackupImport_Click(object sender, RoutedEventArgs e)
    {
        BackupState.Text = "";
        if (PickBackupFile(false, this) is not { Length: > 0 } path) return;
        var preview = SettingsBackup.Check(path, _settings);
        if (!preview.Ok) { BackupState.Text = Loc.T("options.backup.err." + preview.Error); return; }
        var text = Loc.T("options.backup.ask", preview.Changed.Count(c => c != "categories"), preview.CustomCategories, preview.Queues.Count);
        if (preview.Skipped.Count > 0) text += "\n\n" + Loc.T("options.backup.skipped", string.Join(", ", preview.Skipped.Take(8)));
        if (Dialogs.Show(this, text, Loc.T("options.backup.ask_title"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { SettingsBackup.Apply(preview, _settings, _stateDirectory, Queues); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { BackupState.Text = Loc.T("options.error.not_saved"); return; }
        Loc.Apply(_settings.Language);
        if (Application.Current is { } app) ThemeManager.Apply(app, _settings.Theme, _settings.AccentOverride);
        _committed = true; ReopenRequested = true;
        Dialogs.Show(this, Loc.T("options.backup.restored"), Loc.T("options.backup.ask_title"));
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }
}
