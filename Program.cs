using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AiSiteFiller;

static class Program
{
    [STAThread]
    static void Main()
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // Загружаем базовую конфигурацию, а затем переопределяем её локальным файлом
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
            .Build();


        // Создаем фабрику БЕЗ метода .AddConsole() - конфликт заголовков полностью исчезнет!
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddFilter("Microsoft", LogLevel.Warning)
                .AddFilter("System", LogLevel.Warning)
                .AddFilter("AiSiteFiller", LogLevel.Debug);
        });

        System.Windows.Forms.Application.Run(new MainForm(configuration));
    }
}
