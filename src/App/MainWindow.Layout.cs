using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeLauncher;

public partial class MainWindow
{
    public static readonly DependencyProperty IsSidebarProperty = DependencyProperty.Register(
        nameof(IsSidebar), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    private Rect _desktopBounds;
    private WindowState _desktopState;
    private bool _sidebarRight;
    private Point _dragStartScreen;
    private Size _dragStartSize;
    private double _sidebarWidth;
    private double? _sidebarHeight;
    private bool _layoutQueued;
    private LayoutInputs? _lastLayout;

    private readonly record struct LayoutInputs(double Width, double Height, bool Sidebar, int FontPercent,
        bool HasSearch, bool AppearanceOpen, int RunningCount, int RecentCount);

    public bool IsSidebar
    {
        get => (bool)GetValue(IsSidebarProperty);
        private set => SetValue(IsSidebarProperty, value);
    }

    private void FitInitialWindow()
    {
        var work = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, work.Width);
        MinHeight = Math.Min(MinHeight, work.Height);
        Width = Math.Min(Width, work.Width);
        Height = Math.Min(Height, work.Height);
    }

    private void PinLeft_Click(object sender, RoutedEventArgs e) => PinSidebar(right: false);
    private void PinRight_Click(object sender, RoutedEventArgs e) => PinSidebar(right: true);
    private void MoveSidebar_Click(object sender, RoutedEventArgs e) => PinSidebar(!_sidebarRight);

    private void PinSidebar(bool right, string? preferredMonitor = null)
    {
        _sidebarRight = right;
        MoveSidebarButton.SetResourceReference(ContentControl.ContentProperty, right ? "MoveLeft" : "MoveRight");
        AutomationProperties.SetAutomationId(MoveSidebarButton, right ? "DockLeftButton" : "DockRightButton");
        SidebarWidthGrip.HorizontalAlignment = SidebarCornerGrip.HorizontalAlignment =
            right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        SidebarCornerGrip.Cursor = right ? Cursors.SizeNESW : Cursors.SizeNWSE;
        if (IsSidebar)
        {
            MoveSidebarToEdge(right);
            SaveSidebar();
            return;
        }

        _desktopState = WindowState;
        _desktopBounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        IsSidebar = true;
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 0;
        MinHeight = 0;
        _sidebarWidth = _appearance.SidebarWidth;
        _sidebarHeight = _appearance.SidebarHeight;
        MoveSidebarToEdge(right, preferredMonitor);
        SaveSidebar();
        UpdateLayoutMode();
        ApplyWindowAppearance();
    }

    private void MoveSidebarToEdge(bool right, string? preferredMonitor = null)
    {
        if (!DesktopSidebar.MoveToEdge(this, right, _sidebarWidth, preferredMonitor, _sidebarHeight))
        {
            var work = SystemParameters.WorkArea;
            Width = Math.Min(_sidebarWidth, work.Width);
            Height = Math.Min(_sidebarHeight ?? work.Height, work.Height);
            Left = right ? work.Right - Width : work.Left;
            Top = work.Top;
        }
    }

    private void CloseSidebar_Click(object sender, RoutedEventArgs e) => Close();

    private void SaveSidebar()
    {
        var next = _appearance with
        {
            Sidebar = IsSidebar, SidebarRight = _sidebarRight,
            SidebarMonitor = IsSidebar ? DesktopSidebar.MonitorName(this) : _appearance.SidebarMonitor,
            SidebarWidth = _sidebarWidth, SidebarHeight = _sidebarHeight
        };
        if (next == _appearance)
            return;
        _appearance = next;
        if (!next.Save())
            SetStatus("AppearanceSaveFailed");
    }

    private void SidebarGrip_Started(object sender, DragStartedEventArgs e)
    {
        _dragStartScreen = PointToScreen(Mouse.GetPosition(this));
        _dragStartSize = new Size(ActualWidth, ActualHeight);
    }

    private void SidebarGrip_Delta(object sender, DragDeltaEventArgs e)
    {
        var delta = PointToScreen(Mouse.GetPosition(this)) - _dragStartScreen;
        var dpi = VisualTreeHelper.GetDpi(this);
        ResizeSidebar(sender, _dragStartSize, delta.X / dpi.DpiScaleX, delta.Y / dpi.DpiScaleY);
    }

    private void SidebarGrip_Completed(object sender, DragCompletedEventArgs e) => SaveSidebar();

    private void SidebarGrip_KeyDown(object sender, KeyEventArgs e)
    {
        var dx = e.Key switch { Key.Left => -16, Key.Right => 16, _ => 0 };
        var dy = e.Key switch { Key.Up => -16, Key.Down => 16, _ => 0 };
        if ((dx == 0 || sender == SidebarHeightGrip) && (dy == 0 || sender == SidebarWidthGrip))
            return;
        ResizeSidebar(sender, new Size(ActualWidth, ActualHeight), dx, dy);
        SaveSidebar();
        e.Handled = true;
    }

    private void ResizeSidebar(object grip, Size start, double dx, double dy)
    {
        var limit = DesktopSidebar.WorkSize(this);
        if (grip != SidebarHeightGrip)
            _sidebarWidth = Math.Clamp(start.Width + (_sidebarRight ? -dx : dx), Math.Min(300, limit.Width), limit.Width);
        if (grip != SidebarWidthGrip)
        {
            var height = Math.Clamp(start.Height + dy, Math.Min(460, limit.Height), limit.Height);
            _sidebarHeight = height >= limit.Height - 0.5 ? null : height;
        }
        MoveSidebarToEdge(_sidebarRight);
    }

    private void Unpin_Click(object sender, RoutedEventArgs e)
    {
        IsSidebar = false;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        var work = SystemParameters.WorkArea;
        MinWidth = Math.Min(760, work.Width);
        MinHeight = Math.Min(540, work.Height);
        if (!_desktopBounds.IsEmpty && _desktopBounds.Width > 0 && _desktopBounds.Height > 0)
        {
            Left = _desktopBounds.Left;
            Top = _desktopBounds.Top;
            Width = _desktopBounds.Width;
            Height = _desktopBounds.Height;
        }
        WindowState = _desktopState;
        SaveSidebar();
        UpdateLayoutMode();
        ApplyWindowAppearance();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_initialized)
            UpdateLayoutMode();
    }

    private void UpdateLayoutMode()
    {
        if (_layoutQueued || _closing)
            return;
        _layoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _layoutQueued = false;
            if (!_closing)
                ApplyLayoutMode();
        });
    }

    private void ApplyLayoutMode()
    {
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var sidebar = IsSidebar;
        var inputs = new LayoutInputs(width, height, sidebar, _appearance.FontPercent,
            sidebar && HistorySearch.Text.Length != 0, _appearancePanel is not null,
            sidebar ? Math.Min(_liveRows.Count, 3) : 0, sidebar ? Math.Min(RecentSessions.Items.Count, 3) : 0);
        if (_lastLayout == inputs)
            return;
        _lastLayout = inputs;
        var fontScale = _appearance.FontPercent / 100.0;
        var wrappedToolbar = !sidebar && width < 1120 * fontScale;
        var compactSidebarTitle = sidebar && width < 410 * fontScale;
        var wrappedSidebar = sidebar && width < 350 * fontScale;
        var sidebarSpacing = Math.Clamp((height - 550) / 120, 0, 1);
        var shortWindow = height < 730;
        var inset = sidebar ? 14 : width < 1000 ? 22 : 36;
        var wrappedHeader = wrappedToolbar || wrappedSidebar;
        Toolbar.Padding = new Thickness(inset, wrappedHeader ? 6 : 0, inset, wrappedHeader ? 8 : 0);
        Toolbar.MinHeight = sidebar ? 56 : 64;
        Toolbar.SetResourceReference(Border.BackgroundProperty, sidebar ? "SidebarHeader" : "Header");
        WindowTitle.SetResourceReference(TextBlock.FontSizeProperty, sidebar ? "Font22" : width < 1000 ? "Font23" : "Font28");
        if (compactSidebarTitle)
        {
            WindowTitle.Text = "Claude Code";
            UnpinButton.Padding = new Thickness(9, 7, 9, 7);
            AppearanceButton.Padding = UnpinButton.Padding;
        }
        else
        {
            WindowTitle.SetResourceReference(TextBlock.TextProperty, "AppTitle");
            UnpinButton.ClearValue(Control.PaddingProperty);
            AppearanceButton.ClearValue(Control.PaddingProperty);
        }
        var sidebarInset = 8 + 4 * sidebarSpacing;
        Workspace.Margin = new Thickness(inset, sidebar ? sidebarInset : shortWindow ? 12 : 24,
            inset, sidebar ? sidebarInset : shortWindow ? 12 : 20);
        MainToolbar.Visibility = sidebar ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(MainToolbar, wrappedToolbar ? 1 : 0);
        Grid.SetColumn(MainToolbar, wrappedToolbar ? 0 : 1);
        Grid.SetColumnSpan(MainToolbar, wrappedToolbar ? 3 : 1);
        MainToolbar.Margin = new Thickness(0, wrappedToolbar ? 6 : 0, 0, 0);
        Grid.SetColumnSpan(WindowTitle, wrappedSidebar ? 3 : 1);
        Grid.SetRow(UnpinButton, wrappedSidebar ? 1 : 0);
        Grid.SetRow(AppearanceButton, wrappedSidebar ? 1 : 0);
        UnpinButton.Margin = new Thickness(0, wrappedSidebar ? 5 : 0, 0, 0);
        AppearanceButton.Margin = new Thickness(8, wrappedSidebar ? 5 : 0, 0, 0);
        UnpinButton.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        SidebarExit.Visibility = UnpinButton.Visibility;
        DesktopFooter.Visibility = sidebar ? Visibility.Collapsed : Visibility.Visible;
        SidebarFooter.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAutomationId(PinLeftButton, sidebar ? "MainDockLeftButton" : "DockLeftButton");
        AutomationProperties.SetAutomationId(PinRightButton, sidebar ? "MainDockRightButton" : "DockRightButton");
        Dashboard.Margin = new Thickness(0, 0, 0, sidebar ? 8 + 5 * sidebarSpacing : shortWindow ? 16 : 31);
        RunningCaption.Margin = new Thickness(0, 0, 0, sidebar ? 5 + 7 * sidebarSpacing : shortWindow ? 9 : 22);
        RecentCaption.Margin = RunningCaption.Margin;
        TreeCaption.Margin = new Thickness(0, 0, 0, sidebar ? 6 + 4 * sidebarSpacing : shortWindow ? 10 : 16);
        DashboardGap.Width = new GridLength(sidebar ? 0 : width < 1000 ? 18 : 30);
        RecentColumn.Width = sidebar ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(RecentPanel, sidebar ? 0 : 2);
        Grid.SetRow(RecentPanel, sidebar ? 1 : 0);
        RecentPanel.Margin = new Thickness(0, sidebar ? 5 + 8 * sidebarSpacing : 0, 0, 0);
        // Deduct the space for chrome and a tree row, including the margins that grow with the sidebar.
        var sidebarMargins = Workspace.Margin.Top + Workspace.Margin.Bottom + Dashboard.Margin.Bottom +
            RunningCaption.Margin.Bottom + RecentCaption.Margin.Bottom + TreeCaption.Margin.Bottom + RecentPanel.Margin.Top;
        var cardLimit = Math.Max(36, (height - 195 - ProfileTabs.ActualHeight - 82 * fontScale - sidebarMargins) / 2);
        var listHeight = sidebar ? Math.Min(Math.Clamp(height * 0.23, 70, 207), cardLimit)
            : shortWindow ? Math.Clamp((height - 390) * 0.69, 90, 254)
            : Math.Clamp(height * 0.273, 96, 254);
        RunningCard.Height = sidebar ? _liveRows.Count switch
        {
            0 => 50,
            1 or 2 => double.NaN,
            _ => listHeight
        } : listHeight;
        RecentCard.Height = sidebar ? RecentSessions.Items.Count switch
        {
            0 => 50,
            1 or 2 => double.NaN,
            _ => listHeight
        } : listHeight;
        RunningCard.MaxHeight = sidebar ? listHeight : double.PositiveInfinity;
        RecentCard.MaxHeight = RunningCard.MaxHeight;
        RunningCard.Padding = new Thickness(sidebar ? 0 : 13, 0, sidebar ? 0 : 13, 0);
        RecentCard.Padding = RunningCard.Padding;
        if (sidebar)
        {
            RunningCard.Background = Brushes.Transparent;
            RecentCard.Background = Brushes.Transparent;
        }
        else
        {
            RunningCard.SetResourceReference(Border.BackgroundProperty, "Surface");
            RecentCard.SetResourceReference(Border.BackgroundProperty, "Surface");
        }
        RunningCard.BorderThickness = new Thickness(sidebar ? 0 : 1);
        RecentCard.BorderThickness = RunningCard.BorderThickness;
        var stackSearch = sidebar || width < 900 * fontScale;
        Grid.SetRow(SearchBox, stackSearch ? 1 : 0);
        Grid.SetColumn(SearchBox, stackSearch ? 0 : 2);
        Grid.SetColumnSpan(SearchBox, stackSearch ? 3 : 1);
        SearchBox.Width = stackSearch ? double.NaN : Math.Clamp(width * 0.34, 180, 505);
        SearchBox.Margin = stackSearch ? new Thickness(0, 5, 0, 0) : new Thickness(12, 0, 0, 0);
        SearchBox.Visibility = sidebar && HistorySearch.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PinLeftButton.MinWidth = width >= 1200 ? 132 : 0;
        PinRightButton.MinWidth = width >= 1200 ? 140 : 0;
        SetupButton.MinWidth = width >= 1200 ? 158 : 0;
        BrowseButton.MinWidth = width < 1000 ? 110 : 176;
        OpenButton.MinWidth = width < 1000 ? 140 : 244;
        SetupHost.MaxWidth = _appearancePanel is null ? 660 : 480;
        SetupHost.Width = Math.Min(SetupHost.MaxWidth, Math.Max(0, width - 28));
        SetupHost.MaxHeight = Math.Max(0, height - 50);
    }
}
