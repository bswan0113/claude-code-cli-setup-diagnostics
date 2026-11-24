using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace ClaudeLauncher;

public partial class RepairMiniWindow : Window
{
    private SetupPanel? _panel;
    private bool _closeRequested;

    public RepairMiniWindow()
    {
        var appearance = AppearancePreferences.Load();
        AppearanceResources.Apply(appearance);
        InitializeComponent();
        LanguagePicker.SelectedValue = UiLanguage.Current;
        var work = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, work.Width);
        MinHeight = Math.Min(MinHeight, work.Height);
        Width = Math.Min(Width, work.Width);
        Height = Math.Min(Height, work.Height);
        SourceInitialized += (_, _) => LauncherChrome.ApplyDarkCaption(this, appearance.DarkMode);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        await OpenDiagnosticsAsync();
    }

    private async void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_panel is null || _panel.IsBusy || _closeRequested ||
            LanguagePicker.SelectedValue is not string code || code == UiLanguage.Current) return;
        UiLanguage.Apply(code);
        UiLanguage.Save();
        await OpenDiagnosticsAsync();
    }

    private async Task OpenDiagnosticsAsync()
    {
        if (_panel is not null)
        {
            _panel.Finished -= Finish;
            _panel.BusyChanged -= BusyChanged;
        }
        Language = XmlLanguage.GetLanguage(UiLanguage.Current);
        _panel = new SetupPanel(Environment.SystemDirectory, null, null);
        _panel.Finished += Finish;
        _panel.BusyChanged += BusyChanged;
        RepairHost.Content = _panel;
        await _panel.OpenAsync(examine: true, launchFailure: null);
    }

    private void Finish(LaunchIntent? _) => Close();

    private void BusyChanged()
    {
        LanguagePicker.IsEnabled = _panel?.IsBusy == false && !_closeRequested;
        if (_closeRequested && _panel?.IsBusy == false)
            Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) Close(); }));
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_panel?.IsBusy != true) return;
        e.Cancel = true;
        if (_closeRequested) return;
        _closeRequested = true;
        SetResourceReference(TitleProperty, "ClosingAfterWork");
        _panel.CancelPendingReadOrLogin();
    }
}
