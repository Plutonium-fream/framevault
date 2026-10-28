using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FrameVault.Core;

namespace FrameVault.Views;

public sealed class FolderView : UserControl
{
    private readonly WorkspaceSession _session;
    private readonly WorkspaceFolder _folder;
    private readonly Grid _root = new();
    private readonly Button _saveChanges;
    private readonly Button _saveAndLock;
    private readonly TextBox _path = new() { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _summary = Ui.Display("", 22);
    private readonly TextBlock _storageName = Ui.Text("", 13);
    private readonly TextBlock _protection = Ui.Mono("", 10, "Accent");
    private readonly TextBlock _notice = Ui.Text("", 12);
    private readonly TextBlock _migration = Ui.Text("Legacy workspace: saving converts this MP4 to FrameVault 2. Earlier versions cannot open the converted file. Back up the MP4 before your first save.", 12, "Danger");
    private readonly TextBlock _message = Ui.Text("Changes stay in the working folder until you choose Save changes or Save & Lock.", 12, "Muted");

    public FolderView(WorkspaceSession session, WorkspaceFolder folder, Action openFolder, Action saveChanges, Action saveAndLock)
    {
        _session = session;
        _folder = folder;
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(Ui.Mono("FILES  /  OPEN WORKSPACE", 10, "Accent"));
        var title = Ui.Display("Your workspace is open.", 30); title.Margin = new Thickness(0, 10, 0, 8); heading.Children.Add(title);
        heading.Children.Add(Ui.Text("Use File Explorer and your usual apps. FrameVault keeps the MP4 copy safe.", 13, "Muted"));
        Put(heading, 0);

        var notice = new Border { Background = Ui.Brush("AccentSoft"), BorderBrush = Ui.Brush("Accent"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(13, 11, 13, 11), Margin = new Thickness(0, 0, 0, 19), Child = _notice };
        _notice.LineHeight = 19; Put(notice, 1);

        var content = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        _migration.LineHeight = 19; _migration.Margin = new Thickness(0, 0, 0, 17); content.Children.Add(_migration);
        var pathLabel = Ui.Mono("WORKING FOLDER  /  EDIT YOUR FILES HERE", 10); pathLabel.Margin = new Thickness(0, 0, 0, 9); content.Children.Add(pathLabel);
        _path.FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("MonoFont");
        _path.FontSize = 12; _path.MinHeight = 46; _path.ToolTip = "Select this path and press Ctrl+C to copy it.";
        AutomationProperties.SetName(_path, "Working folder path"); content.Children.Add(_path);
        var pathHint = Ui.Text("This is a normal Windows folder, not a mounted drive. The path is also useful if the app closes unexpectedly.", 11, "Muted"); pathHint.LineHeight = 18; pathHint.Margin = new Thickness(0, 8, 0, 20); content.Children.Add(pathHint);

        var details = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        details.ColumnDefinitions.Add(new ColumnDefinition()); details.ColumnDefinitions.Add(new ColumnDefinition());
        var snapshot = new StackPanel(); snapshot.Children.Add(Ui.Mono("LAST SCANNED CONTENTS", 9)); _summary.Margin = new Thickness(0, 9, 14, 0); snapshot.Children.Add(_summary);
        var limit = Ui.Text($"{WorkspaceStore.MaxFileBytes / (1024 * 1024)} MiB per file · {WorkspaceStore.MaxTotalFileBytes / (1024 * 1024)} MiB total", 11, "Muted"); limit.Margin = new Thickness(0, 8, 12, 0); snapshot.Children.Add(limit); details.Children.Add(snapshot);
        var stored = new StackPanel(); stored.Children.Add(Ui.Mono("SAVED INSIDE", 9)); _storageName.Margin = new Thickness(0, 10, 0, 7); _storageName.TextWrapping = TextWrapping.NoWrap; _storageName.TextTrimming = TextTrimming.CharacterEllipsis; stored.Children.Add(_storageName); stored.Children.Add(_protection); Grid.SetColumn(stored, 1); details.Children.Add(stored); content.Children.Add(details);

        var instructions = new Grid();
        for (var i = 0; i < 3; i++) instructions.ColumnDefinitions.Add(new ColumnDefinition());
        AddStep(instructions, 0, "01 / WORK", "Open the folder", "Create folders, add files, and edit in your usual apps.");
        AddStep(instructions, 1, "02 / FINISH", "Close your files", "Let your apps finish writing before saving or locking.");
        AddStep(instructions, 2, "03 / KEEP", "Save & Lock", "Save to the MP4, then remove the open working copy.");
        content.Children.Add(instructions);
        var scroller = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Put(scroller, 2);

        var footer = new StackPanel();
        _message.LineHeight = 18; _message.Margin = new Thickness(0, 0, 0, 12); footer.Children.Add(_message);
        var actions = new Grid();
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var open = Ui.Button("Open folder in Explorer", openFolder, true); open.HorizontalAlignment = HorizontalAlignment.Left; open.MinHeight = 44; actions.Children.Add(open);
        _saveChanges = Ui.Button("Save changes", saveChanges); _saveChanges.MinHeight = 44; _saveChanges.Margin = new Thickness(10, 0, 10, 0); Grid.SetColumn(_saveChanges, 1); actions.Children.Add(_saveChanges);
        _saveAndLock = Ui.Button("Save & Lock", saveAndLock); _saveAndLock.MinHeight = 44; Grid.SetColumn(_saveAndLock, 2); actions.Children.Add(_saveAndLock);
        footer.Children.Add(actions);
        Put(new Border { BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 15, 0, 0), Child = footer }, 3);
        Content = _root;
        Refresh();
    }

    public void Refresh(string? message = null)
    {
        if (!_folder.IsOpen) { ClearSensitive(); return; }
        _path.Text = _folder.WorkPath;
        _migration.Visibility = _session.Data.Photos.Count + _session.Data.Notes.Count + _session.Data.Contacts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _summary.Text = $"{_folder.FileCount:N0} files  ·  {Size(_folder.TotalBytes)}";
        _storageName.Text = Path.GetFileName(_session.FilePath); _storageName.ToolTip = _session.FilePath;
        var requiresReopen = _session.RequiresReopen;
        _saveChanges.IsEnabled = !requiresReopen; _saveAndLock.IsEnabled = !requiresReopen;
        if (requiresReopen)
        {
            _protection.Text = "MP4 PROTECTION NOT VERIFIED";
            _notice.Text = "Saving did not finish cleanly. The MP4 may use a different password or be unencrypted. Keep this working folder, recover your files, and reopen a verified MP4 before saving.";
        }
        else
        {
            _protection.Text = _session.IsProtected ? "PASSWORD PROTECTED" : "NO PASSWORD PROTECTION";
            _notice.Text = _session.IsProtected
                ? "Your MP4 is encrypted. This working folder contains readable files while open; close your files before Save & Lock."
                : "This MP4 has no password. Its contents and the open working folder are readable; choose Add password to protect the MP4.";
        }
        if (!string.IsNullOrWhiteSpace(message)) _message.Text = message;
    }

    public void ClearSensitive()
    {
        _path.Clear(); _summary.Text = _storageName.Text = _protection.Text = _notice.Text = _migration.Text = _message.Text = "";
        _storageName.ToolTip = null; _root.Children.Clear(); Content = null;
    }

    private static void AddStep(Grid grid, int column, string label, string title, string detail)
    {
        var words = new StackPanel { Margin = new Thickness(0, 13, column == 2 ? 0 : 18, 13) };
        words.Children.Add(Ui.Mono(label, 9, "Accent"));
        var heading = Ui.Display(title, 18); heading.Margin = new Thickness(0, 9, 0, 7); words.Children.Add(heading);
        var body = Ui.Text(detail, 11, "Muted"); body.LineHeight = 18; words.Children.Add(body);
        var section = new Border { BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Child = words };
        Grid.SetColumn(section, column); grid.Children.Add(section);
    }
    private void Put(UIElement child, int row) { Grid.SetRow(child, row); _root.Children.Add(child); }
    private static string Size(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:N1} KiB" : $"{bytes / (1024d * 1024):N1} MiB";
}
