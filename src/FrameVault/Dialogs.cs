using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace FrameVault;

internal sealed class CreateDialog : Window
{
    private readonly TextBox _source = new() { IsReadOnly = true };
    private readonly TextBox _name = new() { MaxLength = 120 };
    private readonly TextBox _destination = new() { IsReadOnly = true };
    private readonly CheckBox _protect = new() { Content = "Protect this workspace with a password", IsChecked = true };
    private readonly PasswordBox _password = new() { MaxLength = 1024 };
    private readonly PasswordBox _confirm = new() { MaxLength = 1024 };
    private readonly TextBlock _error = Ui.Text("", 13, "Danger");
    public string SourcePath => _source.Text;
    public string DestinationPath => _destination.Text;
    public string WorkspaceName => _name.Text.Trim();
    public string? WorkspacePassword => _protect.IsChecked == true ? _password.Password : null;

    public CreateDialog(Window owner)
    {
        Owner = owner; Style = (Style)FindResource(typeof(Window));
        NativeWindowAppearance.Attach(this);
        Background = Ui.Brush("Background"); Foreground = Ui.Brush("Text"); Opacity = 1; AllowsTransparency = false;
        Title = "Create workspace · FrameVault"; Width = 650;
        var availableHeight = Math.Max(320, Math.Min(SystemParameters.WorkArea.Height, owner?.ActualHeight > 0 ? owner.ActualHeight : SystemParameters.WorkArea.Height) - 36);
        Height = Math.Min(780, availableHeight);
        MinHeight = Math.Min(520, availableHeight); MinWidth = 550; ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new Grid { Background = Ui.Brush("Background"), Opacity = 1 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Background = Ui.Brush("Background"), Margin = new Thickness(24, 22, 24, 18) };
        header.Children.Add(Ui.Mono("FRAMEVAULT / NEW WORKSPACE", 11, "Muted"));
        var title = Ui.Display("Create workspace", 28); title.Margin = new Thickness(0, 9, 0, 8); header.Children.Add(title);
        header.Children.Add(Ui.Text("Keep your files in one MP4. Work in a familiar folder when unlocked.", 14, "Muted"));
        layout.Children.Add(header);
        var form = new StackPanel { Background = Ui.Brush("Background"), Margin = new Thickness(24, 0, 24, 0) };
        var sourceSection = Section(form, "01", "SOURCE VIDEO");
        sourceSection.Children.Add(Ui.Text("Choose the video that will carry your workspace.", 14, "Muted"));
        PathRow(sourceSection, _source, "Source MP4", Ui.Button("Choose video…", ChooseSource));
        var identitySection = Section(form, "02", "WORKSPACE NAME");
        _name.Margin = new Thickness(0, 1, 0, 0);
        System.Windows.Automation.AutomationProperties.SetName(_name, "Workspace name");
        identitySection.Children.Add(_name);
        var destinationSection = Section(form, "03", "SAVE LOCATION");
        destinationSection.Children.Add(Ui.Text("Save as a new MP4. Your source video stays untouched.", 14, "Muted"));
        PathRow(destinationSection, _destination, "Save new workspace as", Ui.Button("Choose location…", ChooseDestination));
        var securitySection = Section(form, "04", "PASSWORD PROTECTION");
        _protect.Margin = new Thickness(0, 2, 0, 8); securitySection.Children.Add(_protect);
        var passwords = new Grid { Visibility = _protect.IsChecked == true ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 14) };
        passwords.ColumnDefinitions.Add(new ColumnDefinition());
        passwords.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        passwords.ColumnDefinitions.Add(new ColumnDefinition());
        var first = new StackPanel(); Ui.Label(first, "Password · 8+ characters", _password); passwords.Children.Add(first);
        var second = new StackPanel(); Ui.Label(second, "Confirm password", _confirm); Grid.SetColumn(second, 2); passwords.Children.Add(second);
        _protect.Checked += (_, _) => passwords.Visibility = Visibility.Visible;
        _protect.Unchecked += (_, _) => { passwords.Visibility = Visibility.Collapsed; _password.Clear(); _confirm.Clear(); };
        securitySection.Children.Add(passwords);
        securitySection.Children.Add(Ui.Text("Without a password, anyone with the MP4 can read its workspace content.", 14, "Muted"));
        var workingCopy = Ui.Text("While unlocked, files are readable in your private working folder. Use File Explorer to edit them, then choose Save & Lock to save and remove the working copy.", 14, "Muted");
        workingCopy.LineHeight = 21; workingCopy.Margin = new Thickness(0, 12, 0, 0); securitySection.Children.Add(workingCopy);
        var scroller = new ScrollViewer { Background = Ui.Brush("Background"), Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 1); layout.Children.Add(scroller);
        var footer = new StackPanel { Background = Ui.Brush("Background"), Margin = new Thickness(24, 14, 24, 20) };
        _error.Margin = new Thickness(0, 0, 0, 10); footer.Children.Add(_error);
        var actions = new DockPanel();
        var note = Ui.Text("One MP4. All your files.", 12, "Muted"); note.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(note, Dock.Left); actions.Children.Add(note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Ui.Button("Cancel", () => DialogResult = false); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 8, 0); buttons.Children.Add(cancel);
        var create = Ui.Button("Create workspace", Validate, true); create.IsDefault = true; buttons.Children.Add(create);
        actions.Children.Add(buttons); footer.Children.Add(actions);
        Grid.SetRow(footer, 2); layout.Children.Add(footer); Content = layout;
        Loaded += (_, _) => _name.Focus();
    }
    private static StackPanel Section(StackPanel form, string number, string title)
    {
        var panel = new StackPanel { Background = Ui.Brush("Surface") };
        var heading = Ui.Text(number + "  " + title, 12, "Text", FontWeights.SemiBold);
        heading.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(heading);
        var rule = new Border { Background = Ui.Brush("Surface"), BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12), Child = panel };
        form.Children.Add(rule); return panel;
    }
    private static void PathRow(StackPanel parent, TextBox field, string label, Button browse)
    {
        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        field.FontFamily = (FontFamily)Application.Current.FindResource("UiFont"); field.FontSize = 14;
        field.Margin = new Thickness(0, 0, 8, 0); field.VerticalContentAlignment = VerticalAlignment.Center;
        field.TextChanged += (_, _) => field.ToolTip = field.Text;
        System.Windows.Automation.AutomationProperties.SetName(field, label);
        row.Children.Add(field); Grid.SetColumn(browse, 1); row.Children.Add(browse); parent.Children.Add(row);
    }
    private void ChooseSource()
    {
        var dialog = new OpenFileDialog { Title = "Choose the source video", Filter = "MP4 video (*.mp4)|*.mp4", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) _source.Text = dialog.FileName;
    }
    private void ChooseDestination()
    {
        var safeName = string.Concat(WorkspaceName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dialog = new SaveFileDialog { Title = "Save a new workspace MP4", Filter = "MP4 workspace (*.mp4)|*.mp4", DefaultExt = ".mp4", AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(safeName) ? "My workspace.mp4" : safeName + ".mp4", OverwritePrompt = false };
        if (dialog.ShowDialog(this) == true) _destination.Text = dialog.FileName;
    }
    private void Validate()
    {
        if (!File.Exists(SourcePath)) { _error.Text = "Choose an existing MP4 video."; return; }
        if (WorkspaceName.Length == 0) { _error.Text = "Give your workspace a name."; return; }
        if (string.IsNullOrWhiteSpace(DestinationPath)) { _error.Text = "Choose where to save the new MP4."; return; }
        if (File.Exists(DestinationPath)) { _error.Text = "That file already exists. Choose a new filename to keep existing files safe."; return; }
        if (_protect.IsChecked == true && _password.Password.Length < 8) { _error.Text = "Use a password with at least 8 characters."; return; }
        if (_protect.IsChecked == true && _password.Password != _confirm.Password) { _error.Text = "The passwords do not match."; return; }
        DialogResult = true;
    }
}

internal sealed class PasswordDialog : Window
{
    private readonly PasswordBox _password = new() { MaxLength = 1024 };
    private readonly PasswordBox _confirm = new() { MaxLength = 1024 };
    private readonly CheckBox _remove = new() { Content = "Remove password protection" };
    private readonly bool _manage;
    public string? Value => _manage && _remove.IsChecked == true ? null : _password.Password;

    public PasswordDialog(Window owner, bool manage, bool isProtected, string filename)
    {
        Owner = owner; Style = (Style)FindResource(typeof(Window)); _manage = manage;
        NativeWindowAppearance.Attach(this);
        Background = Ui.Brush("Surface"); Foreground = Ui.Brush("Text"); Opacity = 1; AllowsTransparency = false;
        Title = manage ? "Workspace password · FrameVault" : "Unlock workspace · FrameVault";
        Width = 500; SizeToContent = SizeToContent.Height; MaxHeight = SystemParameters.WorkArea.Height - 36;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Background = Ui.Brush("Surface"), Margin = new Thickness(26, 22, 26, 22) };
        panel.Children.Add(Ui.Mono(manage ? "FRAMEVAULT / PASSWORD" : "FRAMEVAULT / UNLOCK", 11, "Muted"));
        var title = Ui.Display(manage ? "Password settings" : "Unlock workspace", 28); title.Margin = new Thickness(0, 11, 0, 12); panel.Children.Add(title);
        var description = Ui.Text(manage ? "Use a strong, unique password. There is no password recovery. Updating protection also saves your working folder; close files in other apps first." : Path.GetFileName(filename), 14, "Muted");
        description.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(description);
        panel.Children.Add(new Border { Height = 1, Background = Ui.Brush("BorderBrush"), Margin = new Thickness(0, 2, 0, 5) });
        Ui.Label(panel, manage ? "New password · at least 8 characters" : "Password", _password);
        if (manage) Ui.Label(panel, "Confirm new password", _confirm);
        if (manage && isProtected)
        {
            panel.Children.Add(_remove);
            _remove.Checked += (_, _) => { _password.IsEnabled = false; _confirm.IsEnabled = false; _password.Clear(); _confirm.Clear(); };
            _remove.Unchecked += (_, _) => { _password.IsEnabled = true; _confirm.IsEnabled = true; };
            var warning = Ui.Text("Removing protection makes workspace content readable to anyone with this MP4.", 14, "Muted");
            panel.Children.Add(warning);
        }
        var error = Ui.Text("", 13, "Danger"); error.Margin = new Thickness(0, 12, 0, 0); panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = Ui.Button("Cancel", () => DialogResult = false); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(cancel);
        var ok = Ui.Button(manage ? "Save password" : "Unlock workspace", () =>
        {
            if (Value is not null && (manage ? Value.Length < 8 : Value.Length == 0)) { error.Text = manage ? "Use at least 8 characters." : "Enter your password."; return; }
            if (manage && Value is not null && Value != _confirm.Password) { error.Text = "The passwords do not match."; return; }
            DialogResult = true;
        }, true);
        ok.IsDefault = true; actions.Children.Add(ok); panel.Children.Add(actions);
        Content = new ScrollViewer { Background = Ui.Brush("Surface"), Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Loaded += (_, _) => _password.Focus();
    }
}
