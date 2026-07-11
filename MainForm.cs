using AiSiteFiller.Application.Interfaces;
using AiSiteFiller.Domain.Entities;
using AiSiteFiller.Infrastructure.Data;
using AiSiteFiller.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Data;
using Label = System.Windows.Forms.Label;

namespace AiSiteFiller;

public class MainForm : Form
{
    private DataGridView _taskGrid = null!;
    private TextBox _logTextBox = null!;
    private Button _btnStart = null!;
    private Button _btnStop = null!;
    private Label _lblStatus = null!;
    private Button _btnGeneratePlan = null!;
    private ComboBox _cmbStatusFilter = null!;
    private ComboBox _cmbSiteFilter = null!;
    private ComboBox _cmbCategoryFilter = null!;
    private DataTable _currentDataTable = null!; // Хранилище данных для мгновенного поиска и сортировки



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
        this.Size = new Size(1000, 700); // Немного увеличим окно для удобства
        this.StartPosition = FormStartPosition.CenterScreen;

        // Увеличиваем высоту панели управления до 100 для двух рядов элементов
        Panel topPanel = new Panel { Dock = DockStyle.Top, Height = 100, BackColor = Color.FromArgb(240, 240, 240) };

        // РЯД 1: Кнопки управления и Статус
        _btnStart = new Button { Text = "▶ ЗАПУСТИТЬ БОТА", Location = new Point(15, 12), Size = new Size(160, 32), BackColor = Color.LightGreen, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        _btnStart.Click += BtnStart_Click;

        _btnStop = new Button { Text = "🛑 ОСТАНОВИТЬ", Location = new Point(190, 12), Size = new Size(130, 32), Enabled = false, BackColor = Color.LightCoral, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        _btnStop.Click += BtnStop_Click;

        _btnGeneratePlan = new Button { Text = "📝 СОЗДАТЬ ПЛАН (ИИ)", Location = new Point(340, 12), Size = new Size(160, 32), BackColor = Color.LightSkyBlue, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        _btnGeneratePlan.Click += BtnGeneratePlan_Click;

        _lblStatus = new Label { Text = "Статус: Робот остановлен", Location = new Point(520, 18), Size = new Size(350, 20), Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = Color.Gray };

        topPanel.Controls.Add(_btnStart);
        topPanel.Controls.Add(_btnStop);
        topPanel.Controls.Add(_btnGeneratePlan);
        topPanel.Controls.Add(_lblStatus);

        // РЯД 2: Фильтры (Выпадающие списки)
        Label lblFilters = new Label { Text = "Фильтры:", Location = new Point(15, 62), AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        topPanel.Controls.Add(lblFilters);

        // Фильтр по Статусу
        _cmbStatusFilter = new ComboBox { Location = new Point(90, 58), Size = new Size(130, 25), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbStatusFilter.Items.AddRange(new object[] { "Все статусы", "Pending", "Processing", "Published", "Failed" });
        _cmbStatusFilter.SelectedIndex = 0;
        _cmbStatusFilter.SelectedIndexChanged += ApplyFiltersAndSorting;

        // Фильтр по Сайту
        _cmbSiteFilter = new ComboBox { Location = new Point(235, 58), Size = new Size(130, 25), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbSiteFilter.Items.AddRange(new object[] { "Все сайты", "tech-info" }); // Сюда можно дописывать новые ID сайтов
        _cmbSiteFilter.SelectedIndex = 0;
        _cmbSiteFilter.SelectedIndexChanged += ApplyFiltersAndSorting;

        // Фильтр по Категории
        _cmbCategoryFilter = new ComboBox { Location = new Point(380, 58), Size = new Size(160, 25), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbCategoryFilter.Items.AddRange(new object[] { "Все категории", "smart-home", "smartphones", "audio", "gadgets" });
        _cmbCategoryFilter.SelectedIndex = 0;
        _cmbCategoryFilter.SelectedIndexChanged += ApplyFiltersAndSorting;

        topPanel.Controls.Add(_cmbCategoryFilter);
        topPanel.Controls.Add(_cmbSiteFilter);
        topPanel.Controls.Add(_cmbStatusFilter);

        // Таблица и логи
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

        _logTextBox = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 180,
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
                            // 1. Вызов OpenAI ИИ — получаем текст статьи
                            string articleHtml = await _aiService.GenerateArticleAsync(task.Topic);

                            // 2. СОХРАНЯЕМ В ЛОКАЛЬНЫЙ АРХИВ POSTGRES: Записываем текст в объект задачи
                            task.ContentHtml = articleHtml;
                            await db.SaveChangesAsync(token); // Мгновенно фиксируем текст в базе на вашем ПК

                            Invoke(() => LogToUi($"💾 [Архив] Статья #{task.Id} успешно сохранена в локальный архив Postgres."));

                            Invoke(() => LogToUi($"🌐 [Сайт] Начинаю сетевую отправку статьи: \"{task.Topic}\""));

                            // 3. Вызов WordPress публикации
                            bool isPublished = await _publisherService.PublishAsync(task.Topic, articleHtml, task.Category, task.SiteId);

                            task.Status = isPublished ? Domain.Enums.TaskStatus.Published : Domain.Enums.TaskStatus.Failed;

                            Invoke(() => LogToUi(isPublished
                                ? $"✅ Статья \"{task.Topic}\" успешно появилась на вашем сайте!"
                                : "❌ Сервер сайта отклонил публикацию. Текст сохранен в архив базы."));
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

            var list = db.ArticlesQueue
                .OrderByDescending(t => t.Id)
                .Take(200) // Берем с запасом последние 200 задач
                .ToList();

            // 1. Создаем структуру таблицы в памяти
            DataTable dt = new DataTable();
            dt.Columns.Add("Id", typeof(int));
            dt.Columns.Add("Topic", typeof(string));
            dt.Columns.Add("Category", typeof(string));
            dt.Columns.Add("SiteId", typeof(string));
            dt.Columns.Add("Status", typeof(string));
            dt.Columns.Add("CreatedAt", typeof(DateTime));

            // 2. Заполняем таблицу данными из Postgres
            foreach (var task in list)
            {
                dt.Rows.Add(task.Id, task.Topic, task.Category, task.SiteId, task.Status.ToString(), task.CreatedAt);
            }

            _currentDataTable = dt;

            // 3. Вызываем метод применения фильтров
            ApplyFiltersAndSorting(null, EventArgs.Empty);
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

    private async void BtnGeneratePlan_Click(object? sender, EventArgs e)
    {
        _btnGeneratePlan.Enabled = false;
        LogToUi("🔮 [Планировщик] Запускаю массовый сбор SEO-тем через ИИ для всех рубрик...");

        try
        {
            // Запускаем сбор пачками по 5 штук для каждой нашей рубрики констант
            int count1 = await _contentPlanner.PopulateQueueWithTrendingTopicsAsync(Domain.Constants.AppCategories.SmartHome, 5);
            LogToUi($"[Планировщик] Добавлено {count1} тем в рубрику Умный Дом.");

            int count2 = await _contentPlanner.PopulateQueueWithTrendingTopicsAsync(Domain.Constants.AppCategories.Smartphones, 5);
            LogToUi($"[Планировщик] Добавлено {count2} тем в рубрику Смартфоны.");

            int count3 = await _contentPlanner.PopulateQueueWithTrendingTopicsAsync(Domain.Constants.AppCategories.Gadgets, 5);
            LogToUi($"[Планировщик] Добавлено {count3} тем в рубрику Гаджеты.");

            LogToUi("🚀 [Планировщик] Контент-план успешно создан! База данных PostgreSQL заполнена.");
            RefreshGrid(); // Перерисовываем таблицу на экране, чтобы увидеть новые задачи
        }
        catch (Exception ex)
        {
            LogToUi($"❌ Ошибка планирования: {ex.Message}");
        }
        finally
        {
            _btnGeneratePlan.Enabled = true;
        }
    }
    private void ApplyFiltersAndSorting(object? sender, EventArgs e)
    {
        if (_currentDataTable == null) return;

        // Создаем представление данных для применения фильтров и сортировки
        DataView dv = new DataView(_currentDataTable);
        var filterParts = new System.Collections.Generic.List<string>();

        // 1. Фильтр по статусу
        if (_cmbStatusFilter.SelectedIndex > 0)
        {
            filterParts.Add($"Status = '{_cmbStatusFilter.SelectedItem}'");
        }

        // 2. Фильтр по сайту
        if (_cmbSiteFilter.SelectedIndex > 0)
        {
            filterParts.Add($"SiteId = '{_cmbSiteFilter.SelectedItem}'");
        }

        // 3. Фильтр по категории
        if (_cmbCategoryFilter.SelectedIndex > 0)
        {
            filterParts.Add($"Category = '{_cmbCategoryFilter.SelectedItem}'");
        }

        // Если есть активные фильтры, объединяем их через AND
        if (filterParts.Count > 0)
        {
            dv.RowFilter = string.Join(" AND ", filterParts);
        }
        else
        {
            dv.RowFilter = ""; // Сброс фильтров (показать всё)
        }

        // Привязываем DataView к сетке. Теперь клики по колонкам для СОРТИРОВКИ заработают автоматически!
        _taskGrid.DataSource = dv;
    }

}
