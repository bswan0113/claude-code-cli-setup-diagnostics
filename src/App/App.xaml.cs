using System.Windows;

namespace ClaudeLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UiLanguage.Apply(UiLanguage.GetInitialLanguage());
#if REPAIR_MINI
        MainWindow = new RepairMiniWindow();
#else
        MainWindow = new MainWindow();
#endif
        MainWindow.Show();
    }
}
