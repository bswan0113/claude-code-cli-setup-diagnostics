using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClaudeLauncher;

public partial class AppearancePanel : UserControl
{
    private bool _ready;
    private AppearancePreferences _shown;

    internal AppearancePanel(AppearancePreferences preferences)
    {
        _shown = preferences;
        InitializeComponent();
        DarkModeToggle.IsChecked = preferences.DarkMode;
        FontScaleSlider.Value = preferences.FontPercent;
        OpacitySlider.Value = preferences.OpacityPercent;
        ShowValues();
        _ready = true;
        Loaded += (_, _) => DarkModeToggle.Focus();
    }

    internal event Action<AppearancePreferences>? Changed;
    internal event Action? Closed;

    internal void ShowSaveResult(bool saved) =>
        SaveFeedback.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        var updated = new AppearancePreferences(DarkModeToggle.IsChecked == true,
            RoundPercent(FontScaleSlider.Value), RoundPercent(OpacitySlider.Value));
        _ready = false;
        FontScaleSlider.Value = updated.FontPercent;
        OpacitySlider.Value = updated.OpacityPercent;
        _ready = true;

        if (updated == _shown)
            return;
        _shown = updated;
        ShowValues();
        Changed?.Invoke(updated);
    }

    private void ShowValues()
    {
        var culture = CultureInfo.GetCultureInfo(UiLanguage.Current);
        FontValue.Text = (_shown.FontPercent / 100.0).ToString("P0", culture);
        OpacityValue.Text = (_shown.OpacityPercent / 100.0).ToString("P0", culture);
    }

    private static int RoundPercent(double value) => (int)Math.Round(value / 5, MidpointRounding.AwayFromZero) * 5;

    private void Close_Click(object sender, RoutedEventArgs e) => Closed?.Invoke();

    private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Closed?.Invoke();
        }
    }
}
