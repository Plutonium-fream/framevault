using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FrameVault.Core;
using FrameVault.Views;
using Microsoft.Win32;

namespace FrameVault;

public sealed partial class MainWindow
{
    private async void CreateWorkspace()
    {
        if (_busy) return;
        var dialog = new CreateDialog(this);
        if (dialog.ShowDialog() != true || !await FinishCurrent()) return;
        _lockedPath = null; ShowStart();
        var source = dialog.SourcePath; var destination = dialog.DestinationPath;
        var name = dialog.WorkspaceName; var password = dialog.WorkspacePassword;
        if (await RunBusy("Creating workspace and preparing your folder…", () =>
        {
            var session = WorkspaceStore.Create(source, destination, name, password);
            PrepareSession(session);
        })) OpenFolder();
    }

    private void BrowseOpen()
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = "Open a FrameVault workspace", Filter = "MP4 workspace (*.mp4)|*.mp4", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) OpenPath(dialog.FileName);
    }

    public async void OpenPath(string path)
    {
        if (_busy || !await FinishCurrent()) return;
        _lockedPath = null; ShowStart();
        var opened = false;
        if (await RunBusy("Reading workspace…", () =>
        {
            var protectedFile = WorkspaceStore.IsProtected(path);
            string? password = null;
            var proceed = true;
            if (protectedFile)
                Dispatcher.Invoke(() =>
                {
                    var dialog = new PasswordDialog(this, false, true, path);
                    proceed = dialog.ShowDialog() == true;
                    if (proceed) password = dialog.Value;
                });
            if (!proceed) { Dispatcher.Invoke(() => _status.Text = "Open cancelled."); return; }
            PrepareSession(WorkspaceStore.Open(path, password));
            opened = true;
        }) && opened) OpenFolder();
    }

    private void PrepareSession(WorkspaceSession session)
    {
        WorkspaceFolder folder;
        try { folder = WorkspaceFolder.Open(session); }
        catch { session.Dispose(); throw; }
        Dispatcher.Invoke(() => AttachSession(session, folder));
    }

    private void AttachSession(WorkspaceSession session, WorkspaceFolder folder)
    {
        _lockedPath = null; _session = session; _folder = folder;
        _folderView = new FolderView(session, folder, OpenFolder, SaveFolder, SaveAndLock);
        _body.Children.Clear(); _body.Children.Add(_folderView);
        Title = session.Data.Name + " · FrameVault";
        ShowFolder();
        _status.Text = "Working folder ready · Changes stay local until you choose Save changes.";
    }

    private void ShowFolder()
    {
        if (_folderView is null || _session is null) return;
        SelectNav("Files"); _pageTitle.Text = _session.Data.Name;
        _pageCaption.Text = "WORKING FOLDER / " + Path.GetFileName(_session.FilePath);
        UpdateTools(); UpdateWorkspaceInfo();
    }

    private void UpdateTools()
    {
        _tools.Children.Clear(); if (_session is null) return;
        var security = Ui.Button(_session.RequiresReopen ? "Protection not verified" : _session.IsProtected ? "Password settings" : "Add password", ManagePassword);
        security.IsEnabled = !_session.RequiresReopen;
        if (_session.RequiresReopen) security.ToolTip = "Keep the working copy and reopen a verified MP4 before changing protection.";
        security.Margin = new Thickness(0, 0, 8, 0); _tools.Children.Add(security);
        var open = Ui.Button("Open MP4…", BrowseOpen); open.ToolTip = "Open another workspace · Ctrl+O"; _tools.Children.Add(open);
    }

    private void UpdateWorkspaceInfo()
    {
        _workspaceInfo.Children.Clear(); if (_session is null || _folder is null) return;
        var stack = new StackPanel(); stack.Children.Add(Ui.Mono(_session.RequiresReopen ? "PROTECTION NOT VERIFIED" : _session.IsProtected ? "MP4 ENCRYPTED" : "MP4 NOT ENCRYPTED", 9, "SidebarMuted"));
        var name = Ui.Display(_session.Data.Name, 17, "SidebarText"); name.Margin = new Thickness(0, 12, 0, 9); name.MaxHeight = 44; name.TextTrimming = TextTrimming.CharacterEllipsis; stack.Children.Add(name);
        var filename = Ui.Mono(Path.GetFileName(_session.FilePath), 10, "SidebarMuted"); filename.TextTrimming = TextTrimming.CharacterEllipsis; filename.TextWrapping = TextWrapping.NoWrap; filename.ToolTip = _session.FilePath; stack.Children.Add(filename);
        var number = Ui.Display(_folder.FileCount.ToString(), 23, "SidebarText"); number.Margin = new Thickness(0, 18, 0, 5); stack.Children.Add(number);
        stack.Children.Add(Ui.Mono("FILES AT LAST SNAPSHOT", 8, "SidebarMuted"));
        _workspaceInfo.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(67, 78, 67)), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(8, 19, 0, 0), Child = stack });
    }

    private void OpenFolder()
    {
        if (_busy || _folder?.IsOpen != true) return;
        OpenExplorer(_folder.WorkPath);
    }

    private void OpenExplorer(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath)) throw new DirectoryNotFoundException("The working folder is no longer available. Do not save an empty replacement; recover your files before continuing.");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
            start.ArgumentList.Add(folderPath);
            Process.Start(start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException) { ShowError(ex); }
    }

    private async void SaveFolder()
    {
        if (_busy || _folder is null) return;
        var folder = _folder;
        if (await RunBusy("Saving the complete working folder to your MP4…", folder.Save))
        {
            var message = "Saved to MP4 · " + DateTime.Now.ToString("t");
            _folderView?.Refresh(message); UpdateWorkspaceInfo(); _status.Text = message;
            ShowSaveWarning(_session?.LastSaveWarning);
        }
    }

    private async void SaveAndLock()
    {
        if (_busy || _folder is null || _session is null) return;
        var path = _session.FilePath; var isProtected = _session.IsProtected;
        if (!await LockCurrent(false)) return;
        if (isProtected) ShowLocked(path);
        else { ShowStart(); _status.Text = "Saved and closed · Working folder removed. This MP4 has no password."; }
    }

    private async Task<bool> LockCurrent(bool discard)
    {
        if (_folder is null) return true;
        var folder = _folder; var session = _session;
        var success = await RunBusy(discard ? "Discarding the working copy and closing…" : "Saving, verifying, and clearing the working copy…", () =>
        {
            if (discard) folder.DiscardAndLock(); else folder.SaveAndLock();
        });
        if (!success) { _folderView?.Refresh("Not locked · Working copy retained. Resolve the error and try again."); return false; }
        ClearReferences();
        if (!discard) ShowSaveWarning(session?.LastSaveWarning);
        return true;
    }

    private async Task<bool> FinishCurrent()
    {
        if (_busy) return false;
        if (_folder is null) return true;
        if (_session?.RequiresReopen == true)
        {
            var recoveryChoice = MessageBox.Show(this,
                "The MP4's current protection could not be verified. Do not discard your working files.\n\nKeep the plaintext working folder for recovery and close this workspace?\n\nYes — Keep the folder and release the session key.\nNo — Stay in this workspace.\n\nWorking folder:\n" + _folder.WorkPath,
                "Keep working copy for recovery?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (recoveryChoice != MessageBoxResult.Yes) return false;
            var folder = _folder;
            if (!await RunBusy("Keeping the working folder for recovery…", folder.LeaveForRecovery)) return false;
            ClearReferences(); return true;
        }
        var answer = MessageBox.Show(this,
            "Close files in other applications before continuing.\n\nYes — Save all folder changes to the MP4, then close the working folder.\nNo — Discard folder changes since the last save and remove the working copy.\nCancel — Keep this workspace open.\n\nDo you want to save your changes?",
            "Save and close workspace?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Cancel) return false;
        return await LockCurrent(answer == MessageBoxResult.No);
    }

    private async void ManagePassword()
    {
        if (_session is null || _folder is null || _busy) return;
        var dialog = new PasswordDialog(this, true, _session.IsProtected, _session.FilePath);
        if (dialog.ShowDialog() != true) return;
        var session = _session; var folder = _folder; var password = dialog.Value;
        var warnings = new List<string>();
        if (await RunBusy("Saving folder contents and updating protection…", () =>
        {
            folder.Save();
            if (session.LastSaveWarning is { } firstWarning) warnings.Add(firstWarning);
            try { session.ChangePassword(password); }
            catch (Exception ex) when (warnings.Count > 0) { throw new IOException(ex.Message + "\n\n" + string.Join("\n", warnings), ex); }
            if (session.LastSaveWarning is { } nextWarning) warnings.Add(nextWarning);
        }))
        {
            UpdateTools(); UpdateWorkspaceInfo(); _folderView?.Refresh("Folder saved · Password settings updated.");
            _status.Text = password is null ? "Password removed. The MP4 is no longer encrypted." : "Password updated. The MP4 is encrypted.";
            ShowSaveWarning(warnings.Count == 0 ? null : string.Join("\n\n", warnings.Distinct()));
        }
    }

    private void ShowSaveWarning(string? warning)
    {
        if (string.IsNullOrWhiteSpace(warning)) return;
        MessageBox.Show(this, warning, "Saved with a recovery copy", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowLocked(string path)
    {
        _lockedPath = path; Title = "Locked · FrameVault"; SelectNav("Workspace");
        _pageTitle.Text = "Workspace locked"; _pageCaption.Text = "WORKING COPY REMOVED / MP4 ENCRYPTED";
        _tools.Children.Clear(); _workspaceInfo.Children.Clear(); _body.Children.Clear();
        var content = new StackPanel { Width = 380, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var seal = new Border { Width = 78, Height = 78, BorderBrush = Ui.Brush("Accent"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center };
        var icon = Ui.Icon("\uE72E", 30, "Accent"); icon.HorizontalAlignment = HorizontalAlignment.Center; seal.Child = icon; content.Children.Add(seal);
        var heading = Ui.Display("Under lock & frame.", 30); heading.HorizontalAlignment = HorizontalAlignment.Center; heading.Margin = new Thickness(0, 24, 0, 10); content.Children.Add(heading);
        var filename = Ui.Text(Path.GetFileName(path), 13, "Muted"); filename.HorizontalAlignment = HorizontalAlignment.Center; content.Children.Add(filename);
        var password = new PasswordBox { MaxLength = 1024 }; Ui.Label(content, "Workspace password", password);
        var unlock = Ui.Button("Unlock workspace", () => Unlock(password), true); unlock.Margin = new Thickness(0, 14, 0, 10); content.Children.Add(unlock);
        password.KeyDown += (_, e) => { if (e.Key == Key.Enter) Unlock(password); };
        var back = Ui.Button("Back to start", () => { _lockedPath = null; ShowStart(); }); back.Style = (Style)FindResource("GhostButton"); content.Children.Add(back);
        _body.Children.Add(content); _status.Text = "Locked · Encrypted MP4 saved; working folder removed; session key cleared.";
        password.Focus();
    }

    private async void Unlock(PasswordBox field)
    {
        if (_busy || _lockedPath is null) return;
        var path = _lockedPath; var password = field.Password; field.Clear();
        if (password.Length == 0) { _status.Text = "Enter your workspace password."; return; }
        if (await RunBusy("Unlocking and preparing your working folder…", () => PrepareSession(WorkspaceStore.Open(path, password)))) OpenFolder();
    }

    private void AddRecoveryNotice(StackPanel page)
    {
        try
        {
            var retained = WorkspaceFolder.FindRetainedFolders();
            if (retained.Count == 0) return;
            var panel = new StackPanel(); panel.Children.Add(Ui.Display("An unfinished working copy is here.", 21));
            var text = Ui.Text("A previous session left " + retained.Count + " working folder(s). These contain plaintext. Keep them until any unsaved work is recovered; FrameVault will not delete them automatically.", 12, "Muted"); text.Margin = new Thickness(0, 9, 0, 12); panel.Children.Add(text);
            var open = Ui.Button("Open recovery location", () => OpenExplorer(WorkspaceFolder.DefaultWorkingRoot)); open.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(open);
            page.Children.Add(new Border { Background = Ui.Brush("AccentSoft"), BorderBrush = Ui.Brush("Accent"), BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(18), Margin = new Thickness(0, 18, 0, 0), Child = panel });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = "The recovery location could not be inspected: " + ex.Message; }
    }

    private void ClearReferences()
    {
        _folderView?.ClearSensitive(); _body.Children.Clear(); _folderView = null;
        _folder = null; _session?.Dispose(); _session = null;
    }

    private async Task<bool> RunBusy(string message, Action work)
    {
        if (_busy) return false;
        _busy = true; _status.Text = message; _body.IsEnabled = false; _tools.IsEnabled = false; _sidebar.IsEnabled = false; Mouse.OverrideCursor = Cursors.Wait;
        try { await Task.Run(work); return true; }
        catch (Exception ex) { ShowError(ex); return false; }
        finally { _busy = false; _body.IsEnabled = true; _tools.IsEnabled = true; _sidebar.IsEnabled = true; Mouse.OverrideCursor = null; }
    }

    private void ShowError(Exception ex)
    {
        _status.Text = "Operation did not finish · Workspace remains open if a working copy is active.";
        if (_folder?.IsOpen == true)
        {
            _folderView?.Refresh("Not locked · Working copy retained. Resolve the error and try again.");
            UpdateTools(); UpdateWorkspaceInfo();
        }
        var message = ex is System.Security.Cryptography.CryptographicException ? "The password is incorrect, or the workspace has been damaged or changed." : ex.Message;
        if (_session?.RequiresReopen == true) message += "\n\nMP4 protection is not verified. The file may use a different password or be unencrypted. Saving and password changes are blocked. Keep your working files and reopen a verified MP4 before continuing.";
        if (_session?.LastSaveWarning is { Length: > 0 } warning && !message.Contains(warning, StringComparison.Ordinal)) message += "\n\n" + warning;
        if (_folder?.IsOpen == true) message += "\n\nYour working copy has been kept at:\n" + _folder.WorkPath + "\n\nClose files in other applications before saving or locking. Do not delete this folder while it contains unsaved work.";
        MessageBox.Show(this, message, "FrameVault", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (!_busy && _folder is null) { _allowClose = true; return; }
        e.Cancel = true;
        if (_busy || !await FinishCurrent()) return;
        _allowClose = true; _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
