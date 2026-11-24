using System.Windows;

namespace ClaudeLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UiLanguage.Apply(UiLanguage.GetInitialLanguage());
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
