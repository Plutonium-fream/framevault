using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FrameVault.Core;
using FrameVault.Views;

namespace FrameVault;

public sealed partial class MainWindow : Window
{
    private WorkspaceSession? _session;
    private WorkspaceFolder? _folder;
    private FolderView? _folderView;
    private string? _lockedPath;
    private bool _busy;
    private bool _allowClose;
    private readonly Grid _body = new();
    private readonly UIElement _sidebar;
    private readonly TextBlock _status = Ui.Text("Ready when you are.", 12, "Muted");
    private readonly Dictionary<string, Button> _nav = new();
    private readonly StackPanel _workspaceInfo = new();
    private readonly TextBlock _pageTitle = Ui.Text("Workspace", 23, weight: FontWeights.Normal);
    private readonly TextBlock _pageCaption = Ui.Text("A little space for everything.", 13, "Muted");
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

    public MainWindow()
    {
        Style = (Style)FindResource(typeof(Window));
        var workArea = SystemParameters.WorkArea;
        Title = "FrameVault";
        Width = Math.Min(1280, workArea.Width - 32); Height = Math.Min(860, workArea.Height - 32);
        MinWidth = Math.Min(920, workArea.Width - 32); MinHeight = Math.Min(620, workArea.Height - 32);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize;
        AllowsTransparency = false; Opacity = 1;
        Background = Ui.Brush("Background"); Foreground = Ui.Brush("Text");
        NativeWindowAppearance.Attach(this);
        var root = new Grid { Background = Ui.Brush("Background") };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        var desktop = new Grid(); desktop.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(198) }); desktop.ColumnDefinitions.Add(new ColumnDefinition());
        _sidebar = BuildSidebar(); desktop.Children.Add(_sidebar);
        var main = new Grid { Margin = new Thickness(30, 22, 30, 18) };
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); main.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Margin = new Thickness(0, 0, 0, 23) };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 18, 0) }; heading.Children.Add(_pageTitle); _pageCaption.Margin = new Thickness(0, 7, 0, 0); heading.Children.Add(_pageCaption);
        _pageTitle.TextWrapping = TextWrapping.NoWrap; _pageTitle.TextTrimming = TextTrimming.CharacterEllipsis; _pageTitle.FontWeight = FontWeights.SemiBold;
        _pageCaption.FontFamily = new FontFamily("Segoe UI"); _pageCaption.FontSize = 12; _pageCaption.TextWrapping = TextWrapping.NoWrap; _pageCaption.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(heading); Grid.SetColumn(_tools, 1); header.Children.Add(_tools); main.Children.Add(header);
        Grid.SetRow(_body, 1); main.Children.Add(_body); Grid.SetColumn(main, 1); desktop.Children.Add(main);
        root.Children.Add(desktop);
        var footer = new Border { BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(22, 6, 22, 0) };
        var footerGrid = new DockPanel(); var version = Ui.Text("FRAMEVAULT  /  FOLDER WORKSPACE", 10, "Muted"); version.FontFamily = new FontFamily("Consolas"); version.HorizontalAlignment = HorizontalAlignment.Right; DockPanel.SetDock(version, Dock.Right); footerGrid.Children.Add(version); footerGrid.Children.Add(_status); footer.Child = footerGrid;
        Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
        Closing += OnClosing;
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key == Key.S) { SaveFolder(); e.Handled = true; }
            if (e.Key == Key.O) { BrowseOpen(); e.Handled = true; }
        };
        ShowStart();
    }

    private UIElement BuildSidebar()
    {
        var panel = new Grid { Margin = new Thickness(17, 27, 17, 20) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition()); panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var upper = new StackPanel();
        var mark = new Grid { Width = 49, Height = 41, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 0, 0, 19) };
        mark.Children.Add(new Border { Width = 38, Height = 29, BorderBrush = Ui.Brush("SidebarMuted"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });
        mark.Children.Add(new Border { Width = 38, Height = 29, Background = Ui.Brush("Sidebar"), BorderBrush = Ui.Brush("SidebarText"), BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
        mark.Children.Add(new Border { Width = 10, Height = 10, Background = Ui.Brush("Accent"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top });
        upper.Children.Add(mark);
        var brand = Ui.Display("FrameVault", 22, "SidebarText"); brand.Margin = new Thickness(10, 0, 0, 6); upper.Children.Add(brand);
        var tag = Ui.Text("THE ARCHIVE DESK", 10, "SidebarMuted"); tag.FontFamily = new FontFamily("Consolas"); tag.Margin = new Thickness(10, 0, 0, 33); upper.Children.Add(tag);
        var home = NavButton("Workspace", "\uE80F", async () =>
        {
            if (_busy) return;
            if (await FinishCurrent()) { _lockedPath = null; ShowStart(); }
        }); upper.Children.Add(home);
        var appsLabel = Ui.Text("WORKING COPY", 10, "SidebarMuted"); appsLabel.FontFamily = new FontFamily("Consolas"); appsLabel.Margin = new Thickness(14, 30, 0, 13); upper.Children.Add(appsLabel);
        upper.Children.Add(NavButton("Files", "\uE8B7", ShowFolder));
        panel.Children.Add(upper);
        Grid.SetRow(_workspaceInfo, 2); panel.Children.Add(_workspaceInfo);
        return new Border { Background = Ui.Brush("Sidebar"), Child = panel };
    }

    private Button NavButton(string name, string glyph, Action action)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(Ui.Icon(glyph, 16, "SidebarMuted")); var label = Ui.Text(name, 13, "SidebarText"); Grid.SetColumn(label, 1); content.Children.Add(label);
        var index = Ui.Text(name == "Files" ? "01" : "", 10, "SidebarMuted"); index.FontFamily = new FontFamily("Consolas"); index.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(index, 2); content.Children.Add(index);
        var button = Ui.Button(name, action); button.Content = content; button.Foreground = Ui.Brush("SidebarText"); button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(12, 13, 8, 13); button.Margin = new Thickness(0, 0, 0, 6); button.BorderThickness = new Thickness(3, 0, 0, 0);
        _nav[name] = button; return button;
    }
    private void SelectNav(string name)
    {
        foreach (var pair in _nav)
        {
            pair.Value.Background = pair.Key == name ? new SolidColorBrush(Color.FromRgb(52, 63, 52)) : Brushes.Transparent;
            pair.Value.BorderBrush = pair.Key == name ? Ui.Brush("Accent") : Brushes.Transparent;
            if (pair.Key != "Workspace") pair.Value.IsEnabled = _session is not null;
        }
    }

    private void ShowStart()
    {
        SelectNav("Workspace"); _pageTitle.Text = "The archive desk"; _pageCaption.Text = "PERSONAL BY DESIGN. PORTABLE BY NATURE."; _tools.Children.Clear(); _body.Children.Clear();
        _workspaceInfo.Children.Clear(); Title = "FrameVault";
        var portable = Ui.Mono("ONE FILE.\nNO FIXED ADDRESS.", 11, "SidebarText"); portable.LineHeight = 18; portable.Margin = new Thickness(10, 0, 0, 12); _workspaceInfo.Children.Add(portable);
        var detail = Ui.Text("Keep your files close.\nCarry them in a video.", 12, "SidebarMuted"); detail.LineHeight = 19; detail.Margin = new Thickness(10, 0, 0, 0); _workspaceInfo.Children.Add(detail);
        var page = new StackPanel();
        var intro = new Grid { Margin = new Thickness(0, 13, 0, 27) };
        intro.ColumnDefinitions.Add(new ColumnDefinition()); intro.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel();
        var headline = Ui.Display("Your files.\nOne moving picture.", 36); headline.LineHeight = 44; copy.Children.Add(headline);
        var blurb = Ui.Text("Unlock in FrameVault. Work in File Explorer.\nSave everything back to your MP4.", 14, "Muted"); blurb.LineHeight = 22; blurb.Margin = new Thickness(0, 16, 20, 0); copy.Children.Add(blurb); intro.Children.Add(copy);
        var stamp = new Border { Width = 92, Height = 92, BorderBrush = Ui.Brush("Accent"), BorderThickness = new Thickness(1), Margin = new Thickness(10, 4, 3, 0), VerticalAlignment = VerticalAlignment.Top, RenderTransform = new RotateTransform(5), RenderTransformOrigin = new Point(.5, .5) };
        var stampText = Ui.Mono("PRIVATE\nARCHIVE\n——\n.MP4", 12, "Accent"); stampText.TextAlignment = TextAlignment.Center; stampText.VerticalAlignment = VerticalAlignment.Center; stampText.LineHeight = 17; stamp.Child = stampText; Grid.SetColumn(stamp, 1); intro.Children.Add(stamp); page.Children.Add(intro);
        var rule = new Border { Height = 1, Background = Ui.Brush("BorderBrush"), Margin = new Thickness(0, 0, 0, 22) }; page.Children.Add(rule);
        var workbench = new Grid { Margin = new Thickness(0, 0, 0, 26) };
        workbench.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.12, GridUnitType.Star) }); workbench.ColumnDefinitions.Add(new ColumnDefinition());
        var actions = new StackPanel { Margin = new Thickness(0, 0, 26, 0) };
        actions.Children.Add(ArchiveAction("01 / START SOMETHING", "Create Workspace", "Choose a video and make a home for your files.", CreateWorkspace, true));
        var open = ArchiveAction("02 / PICK UP WHERE YOU LEFT OFF", "Open Workspace", "Bring an existing FrameVault MP4 back to life.", BrowseOpen, false); open.Margin = new Thickness(0, 12, 0, 0); actions.Children.Add(open);
        workbench.Children.Add(actions); var art = BuildVaultArt(); Grid.SetColumn(art, 1); workbench.Children.Add(art); page.Children.Add(workbench);
        var collectionBand = new Grid();
        collectionBand.ColumnDefinitions.Add(new ColumnDefinition()); collectionBand.ColumnDefinitions.Add(new ColumnDefinition()); collectionBand.ColumnDefinitions.Add(new ColumnDefinition());
        void Add(string number, string title, string text, int column)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 13, 12, 13) };
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal }; var serial = Ui.Mono(number, 10, "Accent"); serial.Margin = new Thickness(0, 5, 10, 0); titleLine.Children.Add(serial); titleLine.Children.Add(Ui.Display(title, 18)); stack.Children.Add(titleLine);
            var body = Ui.Text(text, 11, "Muted"); body.Margin = new Thickness(24, 7, 0, 0); stack.Children.Add(body);
            var section = new Border { BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Child = stack }; Grid.SetColumn(section, column); collectionBand.Children.Add(section);
        }
        Add("01", "Unlock", "Your password opens the archive.", 0); Add("02", "Open folder", "Use Explorer and your usual apps.", 1); Add("03", "Save & Lock", "Store changes, clear the working copy.", 2); page.Children.Add(collectionBand);
        AddRecoveryNotice(page);
        _body.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _status.Text = "Choose Create Workspace or Open Workspace to begin.";
    }

    private Button ArchiveAction(string eyebrow, string title, string description, Action action, bool primary)
    {
        var button = Ui.Button(title, action, primary);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(20, 17, 18, 17); button.MinHeight = 118;
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
        var words = new StackPanel();
        var small = Ui.Mono(eyebrow, 9, primary ? "Surface" : "Muted"); words.Children.Add(small);
        var heading = Ui.Display(title, 23, primary ? "Surface" : "Text"); heading.Margin = new Thickness(0, 8, 0, 7); words.Children.Add(heading);
        var detail = Ui.Text(description, 11, primary ? "Surface" : "Muted"); detail.LineHeight = 17; words.Children.Add(detail); grid.Children.Add(words);
        var arrow = Ui.Text("↗", 23, primary ? "Surface" : "Accent"); arrow.VerticalAlignment = VerticalAlignment.Center; arrow.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(arrow, 1); grid.Children.Add(arrow); button.Content = grid;
        return button;
    }

    private UIElement BuildVaultArt()
    {
        var layout = new Grid { MinHeight = 248 };
        var sheet = new Grid { MaxWidth = 330, Background = Ui.Brush("Raised") };
        sheet.Children.Add(new Border { BorderBrush = Ui.Brush("BorderBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(12) });
        var label = Ui.Mono("CONTENTS / ONE MP4", 9); label.Margin = new Thickness(25, 23, 0, 0); label.HorizontalAlignment = HorizontalAlignment.Left; label.VerticalAlignment = VerticalAlignment.Top; sheet.Children.Add(label);
        var film = new Grid { Width = 176, Height = 156, Background = Ui.Brush("Sidebar"), RenderTransform = new RotateTransform(-6), RenderTransformOrigin = new Point(.5, .5) };
        film.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) }); film.ColumnDefinitions.Add(new ColumnDefinition()); film.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        for (var side = 0; side < 2; side++)
        {
            var holes = new UniformGrid { Rows = 10, Margin = new Thickness(4, 5, 4, 5) };
            for (var i = 0; i < 10; i++) holes.Children.Add(new Border { Background = Ui.Brush("Raised"), Height = 7, CornerRadius = new CornerRadius(1) });
            Grid.SetColumn(holes, side == 0 ? 0 : 2); film.Children.Add(holes);
        }
        var frames = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        for (var i = 0; i < 3; i++) frames.RowDefinitions.Add(new RowDefinition());
        string[] titles = ["UNLOCK", "EXPLORER", "SAVE & LOCK"];
        string[] glyphs = ["\uE72E", "\uE8B7", "\uE74E"];
        for (var i = 0; i < 3; i++)
        {
            var frame = new Grid { Background = Ui.Brush(i == 1 ? "AccentSoft" : "Background"), Margin = new Thickness(0, 2, 0, 2) };
            var icon = Ui.Icon(glyphs[i], 19, i == 1 ? "Accent" : "Text"); icon.Margin = new Thickness(12, 0, 0, 10); icon.HorizontalAlignment = HorizontalAlignment.Left; frame.Children.Add(icon);
            var title = Ui.Mono(titles[i], 8, "Text"); title.Margin = new Thickness(39, 0, 0, 1); title.VerticalAlignment = VerticalAlignment.Center; frame.Children.Add(title);
            Grid.SetRow(frame, i); frames.Children.Add(frame);
        }
        Grid.SetColumn(frames, 1); film.Children.Add(frames); sheet.Children.Add(film);
        var footer = Ui.Mono("ALL YOUR CONTENT. ONE MP4.", 9); footer.Margin = new Thickness(0, 0, 0, 19); footer.VerticalAlignment = VerticalAlignment.Bottom; footer.HorizontalAlignment = HorizontalAlignment.Center; sheet.Children.Add(footer);
        layout.Children.Add(sheet); return layout;
    }

}
