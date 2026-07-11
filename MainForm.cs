using AiSiteFiller.Application.Interfaces;
using AiSiteFiller.Domain.Entities;
using AiSiteFiller.Infrastructure.Data;
using AiSiteFiller.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Drawing;
using System.Linq;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Label = System.Windows.Forms.Label;

namespace AiSiteFiller;

public class MainForm : Form
{
    private DataGridView _taskGrid = null!;
    private TextBox _logTextBox = null!;
    private Button _btnStart = null!;
    private Button _btnStop = null!;
    private Label _lblStatus = null!;

    private CancellationTokenSource? _cts;
    private readonly IConfiguration _configuration;

    // Инфраструктурные сервисы
    private IAiService _aiService = null!;
    private IPublisherService _publisherService = null!;
    private IContentPlannerService _contentPlanner = null!;

    public MainForm(IConfiguration configuration)
    {
        _configuration = configuration;
        InitializeComponent();
        InitializeDependencies();
    }

    private void InitializeComponent()
    {
        this.Text = "Панель управления ИИ-Фабрикой Сайтов";
        this.Size = new Size(950, 650);
        this.StartPosition = FormStartPosition.CenterScreen;

        // Верхняя панель управления
        Panel topPanel = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = Color.FromArgb(240, 240, 240) };

        _btnStart = new Button { Text = "▶ ЗАПУСТИТЬ БОТА", Location = new Point(15, 15), Size = new Size(160, 32), BackColor = Color.LightGreen, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        _btnStart.Click += BtnStart_Click;

        _btnStop = new Button { Text = "🛑 ОСТАНОВИТЬ", Location = new Point(190, 15), Size = new Size(130, 32), Enabled = false, BackColor = Color.LightCoral, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        _btnStop.Click += BtnStop_Click;

        _lblStatus = new Label { Text = "Статус: Робот остановлен", Location = new Point(340, 22), Size = new Size(300, 20), Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = Color.Gray };

        topPanel.Controls.Add(_btnStart);
        topPanel.Controls.Add(_btnStop);
        topPanel.Controls.Add(_lblStatus);

        // Центральная таблица для задач из PostgreSQL
        _taskGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.White,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };

        // Нижнее поле логов
        _logTextBox = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 200,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(20, 20, 20),
            ForeColor = Color.LightGreen,
            Font = new Font("Consolas", 9.5f)
        };

        this.Controls.Add(_taskGrid);
        this.Controls.Add(_logTextBox);
        this.Controls.Add(topPanel);
    }

    private void InitializeDependencies()
    {
        // Создаем локальную изолированную фабрику логов для наших внутренних сервисов
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddFilter("AiSiteFiller", LogLevel.Debug);
        });

        // Инициализируем сервисы, передавая им строго изолированные логеры
        _aiService = new OpenAiGptService(_configuration, loggerFactory.CreateLogger<OpenAiGptService>());
        _publisherService = new WordPressPublisherService(_configuration, loggerFactory.CreateLogger<WordPressPublisherService>());
        _contentPlanner = new ContentPlannerService(_aiService);

        LogToUi("⚙️ Синхронизация структуры базы данных PostgreSQL через EF Core...");

        try
        {
            using var db = new AppDbContext();
            db.Database.Migrate();
            LogToUi("✅ База данных успешно синхронизирована. Миграции проверены.");
        }
        catch (Exception ex)
        {
            LogToUi($"❌ Критическая ошибка инициализации БД: {ex.Message}");
        }

        LogToUi("Система инициализирована. Ожидание запуска...");
        RefreshGrid();
    }

    private void BtnStart_Click(object? sender, EventArgs e)
    {
        _cts = new CancellationTokenSource();
        _btnStart.Enabled = false;
        _btnStop.Enabled = true;
        _lblStatus.Text = "Статус: Автоматическая генерация контента...";
        _lblStatus.ForeColor = Color.DarkGreen;

        LogToUi("▶ Фоновая служба конвейера успешно запущена.");

        // Запускаем бесконечный рабочий цикл в отдельном потоке
        Task.Run(() => WorkerProcessLoopAsync(_cts.Token));
    }

    private void BtnStop_Click(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
        _lblStatus.Text = "Статус: Останавливаюсь...";
        _lblStatus.ForeColor = Color.DarkRed;
    }

    private async Task WorkerProcessLoopAsync(CancellationToken token)
    {
        try
        {
            using (var db = new AppDbContext())
            {
                Invoke(() => LogToUi("⚙️ Синхронизация структуры базы данных PostgreSQL через EF Core..."));
                await db.Database.MigrateAsync(token);

                // Автоматический сброс упавших задач обратно в очередь (только для режима отладки)
#if DEBUG
                Invoke(() => LogToUi("[DEBUG] Автоматический сброс задач из статуса Failed в Pending..."));
                var failedTasks = await db.ArticlesQueue
                    .Where(t => t.Status == Domain.Enums.TaskStatus.Failed)
                    .ToListAsync(token);

                if (failedTasks.Any())
                {
                    foreach (var failedTask in failedTasks)
                    {
                        failedTask.Status = Domain.Enums.TaskStatus.Pending;
                    }
                    await db.SaveChangesAsync(token);
                    Invoke(() => LogToUi($"[DEBUG] Успешно возвращено в очередь задач: {failedTasks.Count} шт."));
                }
#endif

                Invoke(RefreshGrid);

                if (!await db.ArticlesQueue.AnyAsync(token))
                {
                    Invoke(() => LogToUi("Queue is empty. AI is planning starting topics..."));
                    await _contentPlanner.PopulateQueueWithTrendingTopicsAsync(Domain.Constants.AppCategories.SmartHome, 3);
                    await _contentPlanner.PopulateQueueWithTrendingTopicsAsync(Domain.Constants.AppCategories.Smartphones, 3);
                    Invoke(RefreshGrid);
                }
            }



            while (!token.IsCancellationRequested)
            {
                using (var db = new AppDbContext())
                {
                    // Ищем задачу в Postgres
                    var task = await db.ArticlesQueue
                        .Where(t => t.Status == Domain.Enums.TaskStatus.Pending)
                        .OrderBy(t => t.Id)
                        .FirstOrDefaultAsync(token);

                    if (task != null)
                    {
                        Invoke(() => LogToUi($"⚡ Взята задача #{task.Id}: \"{task.Topic}\""));

                        task.Status = Domain.Enums.TaskStatus.Processing;
                        await db.SaveChangesAsync(token);
                        Invoke(RefreshGrid);

                        try
                        {
                            // 1. Вызов OpenAI ИИ
                            string articleHtml = await _aiService.GenerateArticleAsync(task.Topic);

                            // Выводим информацию на экран WinForms в безопасном режиме
                            Invoke(() => LogToUi($"🌐 [Сайт] Начинаю сетевую отправку статьи: \"{task.Topic}\""));
                            
                            // 2. Вызов мультисайтовой WordPress публикации
                            bool isPublished = await _publisherService.PublishAsync(task.Topic, articleHtml, task.Category, task.SiteId);

                            task.Status = isPublished ? Domain.Enums.TaskStatus.Published : Domain.Enums.TaskStatus.Failed;

                            // Красивый лог на экран
                            Invoke(() => LogToUi(isPublished
                                ? $"✅ Статья \"{task.Topic}\" успешно появилась на вашем сайте!"
                                : "❌ Сервер сайта отклонил публикацию (проверьте логин/пароль приложения)."));
                        }
                        catch (Exception ex)
                        {
                            // Выводим не только сообщение, но и StackTrace, чтобы понять, какой метод вызвал ошибку
                            Invoke(() => LogToUi($"❌ Критическая ошибка: {ex.Message}"));
                            Invoke(() => LogToUi($"🔍 Стек ошибки: {ex.StackTrace}"));
                            task.Status = Domain.Enums.TaskStatus.Failed;
                        }

                        await db.SaveChangesAsync(token);
                        Invoke(RefreshGrid);
                    }
                    else
                    {
                        Invoke(() => LogToUi("🔄 В очереди нет задач. Ожидание..."));
                    }
                }

                // Интервал фонового сна (например, 20 секунд для теста работы)
                await Task.Delay(TimeSpan.FromSeconds(20), token);
            }
        }
        catch (TaskCanceledException)
        {
            Invoke(() =>
            {
                _lblStatus.Text = "Статус: Робот остановлен";
                _lblStatus.ForeColor = Color.Gray;
                LogToUi("🛑 Работа конвейера принудительно остановлена пользователем.");
            });
        }
    }

    private void RefreshGrid()
    {
        try
        {
            using var db = new AppDbContext();
            // Подтягиваем последние 50 задач, чтобы не перегружать память
            var list = db.ArticlesQueue
                .OrderByDescending(t => t.Id)
                .Take(50)
                .ToList();

            _taskGrid.DataSource = list;
        }
        catch (Exception ex)
        {
            LogToUi($"⚠️ Не удалось обновить таблицу данных: {ex.Message}");
        }
    }

    private void LogToUi(string message)
    {
        if (_logTextBox.IsDisposed) return;
        _logTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}
