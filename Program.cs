using Microsoft.Extensions.Configuration;

namespace AiSiteFiller;

static class Program
{
    [STAThread]
    static void Main()
    {
        // 1. Вручную включаем базовые настройки WinForms (вместо ApplicationConfiguration.Initialize())
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        // Настройка HighDpiMode для поддержки современных 2K/4K мониторов (опционально)
        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // 2. Загружаем конфигурацию JSON из корня приложения
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // 3. Запускаем форму интерфейса
        System.Windows.Forms.Application.Run(new MainForm(configuration));
    }
}
