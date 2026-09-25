using System.Windows;
using System.Windows.Threading;

namespace BotClientDriverHostUi;

/// <summary>
/// 图形宿主入口。界面里所有耗时动作都在后台线程执行，这里只兜住"漏出来的异常"，
/// 避免一个日志格式化错误就把挂机进程整个掀翻。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "界面线程出现未处理异常（挂机可能仍在运行）：\n\n" + args.Exception.Message,
                "BotClientDriverHostUi",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                MessageBox.Show(
                    "后台线程出现未处理异常（进程即将退出）：\n\n" + ex.Message,
                    "BotClientDriverHostUi",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };
    }
}
