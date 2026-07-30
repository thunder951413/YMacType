using System.Windows;

namespace YMacType.Settings
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (System.Array.Exists(
                    e.Args,
                    argument => argument.Equals(
                        "--install",
                        System.StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var result = Installer.Install();
                    MessageBox.Show(
                        result,
                        "YMacType 安装完成",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    Shutdown(0);
                    return;
                }
                catch (System.Exception exception)
                {
                    MessageBox.Show(
                        exception.Message,
                        "YMacType 安装失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    Shutdown(1);
                    return;
                }
            }

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
    }
}
