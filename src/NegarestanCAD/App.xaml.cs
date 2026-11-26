using System.Windows;
using System.Windows.Media;

namespace NegarestanCAD;

public partial class App : Application
{
    public static FontFamily PersianFontFamily { get; private set; } = new("Vazirmatn, Tahoma, Segoe UI");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (Resources["VazirmatnFont"] is FontFamily ff)
            PersianFontFamily = ff;

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"خطای غیرمنتظره رخ داد:\n{args.Exception.Message}",
                "نگارستان کَد - خطا",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                MessageBoxResult.OK,
                MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            args.Handled = true;
        };
    }
}
