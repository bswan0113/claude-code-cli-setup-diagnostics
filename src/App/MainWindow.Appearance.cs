using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClaudeLauncher;

public partial class MainWindow
{
    private AppearancePreferences _appearance = new();
    private AppearancePanel? _appearancePanel;

    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _setup is not null || _appearancePanel is not null)
            return;

        var panel = new AppearancePanel(_appearance);
        _appearancePanel = panel;
        panel.Changed += ChangeAppearance;
        panel.Closed += FinishAppearance;
        ShowOverlay(panel);
    }

    private void ChangeAppearance(AppearancePreferences next)
    {
        next = _appearance with { DarkMode = next.DarkMode, FontPercent = next.FontPercent,
            OpacityPercent = next.OpacityPercent };
        _appearance = next;
        AppearanceResources.Apply(next);
        ApplyWindowAppearance();
        UpdateLayoutMode();
        _appearancePanel?.ShowSaveResult(next.Save());
    }

    private void ApplyWindowAppearance()
    {
        LauncherOpacity.Apply(this, _appearance.OpacityPercent);
        LauncherChrome.ApplyDarkCaption(this, _appearance.DarkMode);
    }

    private void FinishAppearance()
    {
        if (_appearancePanel is not { } panel)
            return;

        panel.Changed -= ChangeAppearance;
        panel.Closed -= FinishAppearance;
        _appearancePanel = null;
        HideOverlay();
        if (_setupQueue.Count > 0)
            Dispatcher.BeginInvoke(OpenNextSetup);
    }

    private void ShowOverlay(UserControl panel)
    {
        _returnFocus = Keyboard.FocusedElement;
        SetupHost.Content = panel;
        SetupOverlay.Visibility = Visibility.Visible;
        LauncherSurface.IsEnabled = false;
        UpdateLayoutMode();
    }

    private void HideOverlay()
    {
        SetupHost.Content = null;
        SetupOverlay.Visibility = Visibility.Collapsed;
        LauncherSurface.IsEnabled = true;
        UpdateLayoutMode();
        if (_returnFocus is not UIElement { IsVisible: true, IsEnabled: true, Focusable: true } previous ||
            Keyboard.Focus(previous) != previous)
        {
            AppearanceButton.Focus();
        }
        _returnFocus = null;
    }
}
