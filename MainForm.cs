using AiSiteFiller.Application.Interfaces;
using AiSiteFiller.Domain.Entities;
using AiSiteFiller.Infrastructure.Data;
using AiSiteFiller.Infrastructure.Services;
using DnsClient.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Data;
using ILoggerFactory = Microsoft.Extensions.Logging.ILoggerFactory;
using Label = System.Windows.Forms.Label;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

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
    private DataTable _currentDataTable = null!;
    private Button _btnAddTask = null!;


    private CancellationTokenSource? _cts;
    private readonly IConfiguration _configuration;
    private ILoggerFactory _loggerFactory = null!;


    // Инфраструктурные сервисы
    private IAiService _aiService = null!;
    private List<IPublisherService> _publishers = null!;
    private IContentPlannerService _contentPlanner = null!;

    public MainForm(IConfiguration configuration)
    {
        _configuration = configuration;
        InitializeComponent();
        InitializeDependencies();

        // Добавьте эту строчку, чтобы безопасно освобождать память СУБД и логов
        this.FormClosed += (s, e) => _loggerFactory?.Dispose();
    }


    private void InitializeComponent()
    {
        this.Text = "Панель управления ИИ-Фабрикой Сайтов";
        this.Size = new Size(1050, 700);
        this.StartPosition = FormStartPosition.CenterScreen;

        Panel topPanel = new Panel { Dock = DockStyle.Top, Height = 100, BackColor = Color.FromArgb(240, 240, 240) };

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
        _btnAddTask = new Button { Text = "➕ ДОБАВИТЬ ТЕМУ", Location = new Point(515, 12), Size = new Size(140, 32), BackColor = Color.LightYellow, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        _btnAddTask.Click += BtnAddTask_Click;
        topPanel.Controls.Add(_btnAddTask);

        // Сдвиньте текстовый статус чуть правее (на координату X = 670), чтобы элементы не накладывались:
        _lblStatus = new Label { Text = "Статус: Робот остановлен", Location = new Point(670, 18), Size = new Size(350, 20), Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = Color.Gray };

        topPanel.Controls.Add(_lblStatus);

        Label lblFilters = new Label { Text = "Фильтры:", Location = new Point(15, 62), AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        topPanel.Controls.Add(lblFilters);

        _cmbStatusFilter = new ComboBox { Location = new Point(90, 58), Size = new Size(130, 25), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbStatusFilter.Items.AddRange(new object[] { "Все статусы", "Pending", "Processing", "Published", "Failed" });
        _cmbStatusFilter.SelectedIndex = 0;
        _cmbStatusFilter.SelectedIndexChanged += ApplyFiltersAndSorting;

        _cmbSiteFilter = new ComboBox { Location = new Point(235, 58), Size = new Size(130, 25), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbSiteFilter.Items.AddRange(new object[] { "Все сайты", "tech-info", "vk-group" });
        _cmbSiteFilter.SelectedIndex = 0;
        _cmbSiteFilter.SelectedIndexChanged += ApplyFiltersAndSorting;

        _cmbCategoryFilter = new ComboBox { Location = new Point(380, 58), Size = new Size(160, 25), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbCategoryFilter.Items.AddRange(new object[] { "Все категории", "smart-home", "smartphones", "audio", "gadgets" });
        _cmbCategoryFilter.SelectedIndex = 0;
        _cmbCategoryFilter.SelectedIndexChanged += ApplyFiltersAndSorting;

        topPanel.Controls.Add(_cmbStatusFilter);
        topPanel.Controls.Add(_cmbSiteFilter);
        topPanel.Controls.Add(_cmbCategoryFilter);

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
        _taskGrid.CellDoubleClick += TaskGrid_CellDoubleClick;

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
        _loggerFactory = LoggerFactory.Create(builder => { builder.AddFilter("AiSiteFiller", LogLevel.Debug); });

        _aiService = new OpenAiGptService(_configuration, _loggerFactory.CreateLogger<OpenAiGptService>());
        _contentPlanner = new ContentPlannerService(_aiService);

        // Инициализируем и упаковываем всех издателей в единый веерный массив
        _publishers = new List<IPublisherService>
        {
            new WordPressPublisherService(_configuration, _loggerFactory.CreateLogger<WordPressPublisherService>()),
            new VkPublisherService(_configuration, _loggerFactory.CreateLogger<VkPublisherService>())
            // Сюда в будущем в одну строчку добавятся: new TelegramPublisherService(...), new DzenPublisherService(...)
        };

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
        _btnGeneratePlan.Enabled = true;
        _lblStatus.Text = "Статус: Робот остановлен";
        _lblStatus.ForeColor = Color.Gray;
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
                            string articleHtml = string.Empty;
                            byte[]? imageBytes = null;

                            if (string.IsNullOrEmpty(task.ContentHtml))
                            {
                                Invoke(() => LogToUi("📝 [ИИ] Генерирую новый HTML-текст статьи через ProxyAPI..."));
                                articleHtml = await _aiService.GenerateArticleAsync(task.Topic);

                                task.ContentHtml = articleHtml;
                                db.ArticlesQueue.Update(task);
                                await db.SaveChangesAsync();
                                Invoke(() => LogToUi($"Статья #{task.Id} успешно заархивирована в локальный Postgres."));
                            }
                            else
                            {
                                Invoke(() => LogToUi("♻️ [Архив] Обнаружен готовый текст статьи. Пропускаю вызов Текстового ИИ."));
                                articleHtml = task.ContentHtml;
                            }

                            IFileStorageService mongoStorage = new MongoDbStorageService(_configuration, _loggerFactory.CreateLogger<MongoDbStorageService>());
                            if (string.IsNullOrEmpty(task.MongoImageId))
                            {
                                Invoke(() => LogToUi("🎨 [Локальный ИИ] Создаю уникальную нейроиллюстрацию в Stable Diffusion..."));
                                try
                                {
                                    string base64Image = await _aiService.GenerateImageAsync(task.Topic);
                                    if (!string.IsNullOrEmpty(base64Image))
                                    {
                                        imageBytes = Convert.FromBase64String(base64Image);


                                        string mongoId = await mongoStorage.SaveFileAsync(imageBytes, $"{task.Id}_cover.jpg", task.Category ?? "");
                                        task.MongoImageId = mongoId;
                                        db.ArticlesQueue.Update(task);
                                        Invoke(() => LogToUi("💾 [Медиа-Архив] Обложка успешно сохранена в MongoDB."));
                                    }
                                }
                                catch (Exception imgEx)
                                {
                                    Invoke(() => LogToUi("⚠️ Сбой обработки картинки (пропускаю): " + imgEx.Message));
                                }
                            }
                            else
                            {
                                Invoke(() => LogToUi("♻️ [Архив] Обложка этой статьи уже есть в MongoDB. Пропускаю вызов Stable Diffusion."));
                                // Передаем null, так как на сайт картинка уже залита, а ВК мы отладим на отправку чистого текста
                                imageBytes = await mongoStorage.GetFileAsync(task.MongoImageId);
                            }
                            await db.SaveChangesAsync();

                            // Дальше без изменений продолжается ваш родной цикл веерной рассылки
                            Invoke(() => LogToUi("🚀 [Веерный конвейер] Запускаю автоматическую рассылку по всем платформам..."));

                            int successCount = 0;

                            foreach (var publisher in _publishers)
                            {
                                try
                                {
                                    bool isPublished = await publisher.PublishAsync(task.Topic, articleHtml, task.Category, task.SiteId, imageBytes);
                                    if (isPublished) successCount++;
                                }
                                catch (Exception pubEx)
                                {
                                    // Безопасно маршалируем вызов окна в главный поток WinForms, предотвращая крэш приложения
                                    this.BeginInvoke(new Action(() =>
                                    {
                                        LogToUi("⚠️ Сбой платформы публикации: " + pubEx.Message);

                                        MessageBox.Show(
                                            "Произошел контролируемый сбой при веерной рассылке контента:\n\n" + pubEx.Message,
                                            "Информация конвейера",
                                            MessageBoxButtons.OK,
                                            MessageBoxIcon.Warning
                                        );
                                    }));
                                }
                            }

                            task.Status = (successCount > 0) ? Domain.Enums.TaskStatus.Published : Domain.Enums.TaskStatus.Failed;


                            Invoke(new Action(() => LogToUi(task.Status == Domain.Enums.TaskStatus.Published
                                ? "✅ Статья успешно разлетелась по медиа-каналам (" + successCount + "/" + _publishers.Count + " успешно)!"
                                : "❌ Все платформы отклонили запрос. Текст сохранен в архив Postgres.")));

                        }

                        catch (Exception ex)
                        {
                            Invoke(new Action(() => LogToUi("Критическая ошибка: " + ex.Message)));
                            Invoke(new Action(() => LogToUi("Стек ошибки: " + ex.StackTrace)));
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
                .Take(200)
                .ToList();

            DataTable dt = new DataTable();
            dt.Columns.Add("Id", typeof(int));
            dt.Columns.Add("Topic", typeof(string));
            dt.Columns.Add("Category", typeof(string));
            dt.Columns.Add("SiteId", typeof(string));
            dt.Columns.Add("Status", typeof(string));
            dt.Columns.Add("CreatedAt", typeof(DateTime));

            foreach (var task in list)
            {
                dt.Rows.Add(task.Id, task.Topic, task.Category, task.SiteId, task.Status.ToString(), task.CreatedAt);
            }

            _currentDataTable = dt;
            ApplyFiltersAndSorting(null, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogToUi("Не удалось обновить таблицу данных: " + ex.Message);
        }
    }

    private void ApplyFiltersAndSorting(object? sender, EventArgs e)
    {
        if (_currentDataTable == null) return;

        DataView dv = new DataView(_currentDataTable);
        var filterParts = new List<string>();

        if (_cmbStatusFilter.SelectedIndex > 0)
        {
            filterParts.Add("Status = '" + _cmbStatusFilter.SelectedItem + "'");
        }

        if (_cmbSiteFilter.SelectedIndex > 0)
        {
            filterParts.Add("SiteId = '" + _cmbSiteFilter.SelectedItem + "'");
        }

        if (_cmbCategoryFilter.SelectedIndex > 0)
        {
            filterParts.Add("Category = '" + _cmbCategoryFilter.SelectedItem + "'");
        }

        if (filterParts.Count > 0)
        {
            dv.RowFilter = string.Join(" AND ", filterParts);
        }
        else
        {
            dv.RowFilter = "";
        }

        _taskGrid.DataSource = dv;

        foreach (DataGridViewColumn column in _taskGrid.Columns)
        {
            column.SortMode = DataGridViewColumnSortMode.Automatic;
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
    private void TaskGrid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        // Игнорируем клики по шапке таблицы (индекс строки -1)
        if (e.RowIndex < 0) return;

        try
        {
            // Вытаскиваем Id и Тему прямо из выделенной строки DataGridView
            var selectedRow = _taskGrid.Rows[e.RowIndex];
            int taskId = Convert.ToInt32(selectedRow.Cells["Id"].Value);
            string topic = selectedRow.Cells["Topic"].Value?.ToString() ?? "Без темы";

            // Открываем наше новое адаптивное окно просмотра
            using var viewerForm = new ArticleViewerForm(taskId, topic);
            viewerForm.ShowDialog(this); // Режим ShowDialog заблокирует заднее окно, пока вы читаете текст
        }
        catch (Exception ex)
        {
            LogToUi("⚠️ Ошибка при открытии окна просмотра: " + ex.Message);
        }
    }
    private void BtnAddTask_Click(object? sender, EventArgs e)
    {
        using var addForm = new AddTaskForm();

        // Если пользователь заполнил поля и нажал "Сохранить"
        if (addForm.ShowDialog(this) == DialogResult.OK)
        {
            LogToUi("✏️ [Очередь] В базу данных вручную добавлена новая задача.");
            RefreshGrid(); // Мгновенно обновляем интерактивную сетку на экране
        }
    }


}
