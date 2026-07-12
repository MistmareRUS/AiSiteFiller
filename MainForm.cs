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
    private DataGridView dgvDetails = null!; // Таблица для отображения подзадач веера



    private CancellationTokenSource? _cts;
    private readonly IConfiguration _configuration;
    private readonly IFileStorageService _mongoDbStorageService;

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
        _mongoDbStorageService = new MongoDbStorageService(_configuration, _loggerFactory.CreateLogger<MongoDbStorageService>());

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
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            Location = new Point(20, 95),
            Size = new Size(500, 240),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
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

        // Настройка таблицы детализации подзадач веера
        dgvDetails = new DataGridView
        {
            Location = new Point(540, 45), // Аккуратно встает справа от _dgv с отступом 20px
            Size = new Size(420, 480),     // Занимает всю правую часть экрана
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            BackgroundColor = Color.White,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        // КРИТИЧЕСКИЙ ШАГ: Подписываемся на событие клика по строке главной таблицы статей
        _taskGrid.SelectionChanged += DgvTasks_SelectionChanged;

        // Не забудьте добавить элемент управления на саму форму:
        this.Controls.Add(dgvDetails);


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


    private async Task WorkerProcessLoopAsync(System.Threading.CancellationToken cancellationToken)
    {
        Invoke(() => LogToUi("🚀 Фоновая служба конвейера подзадач запущена. Ожидаю очереди..."));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                PublicationTask? pubTask = null;

                // 1. Берем ОДНУ конкретную подзадачу (например, только для VK или только для WP)
                using (var db = new AppDbContext())
                {
                    pubTask = await db.GetNextPendingPublicationTaskAsync();
                }

                if (pubTask == null)
                {
                    Invoke(() => LogToUi("💤 В очереди нет подзадач со статусом Pending. Засыпаю на 30 секунд..."));
                    await Task.Delay(30000, cancellationToken);
                    continue;
                }

                // Извлекаем родительскую статью, к которой привязана эта публикация
                var articleTask = pubTask.ArticleTask;
                Invoke(() => LogToUi($"⚡ Взята подзадача #{pubTask.Id} для платформы [{pubTask.Platform}] (Статья #{articleTask.Id}: \"{articleTask.Topic}\")"));

                string articleHtml = articleHtml = articleTask.ContentHtml ?? string.Empty;
                byte[]? imageBytes = null;

                // 2. УМНЫЙ ТЕКСТОВЫЙ КЭШ: Если текст статьи в родительской записи пуст — генерируем через ИИ
                if (string.IsNullOrEmpty(articleHtml))
                {
                    Invoke(() => LogToUi("📝 [ИИ] Генерирую новый HTML-текст статьи через ProxyAPI..."));
                    articleHtml = await _aiService.GenerateArticleAsync(articleTask.Topic);

                    // Кэшируем текст в родительскую запись в Postgres
                    articleTask.ContentHtml = articleHtml;
                    using (var dbContext = new AppDbContext())
                    {
                        dbContext.ArticlesQueue.Update(articleTask);
                        await dbContext.SaveChangesAsync();
                    }
                    Invoke(() => LogToUi($"Статья #{articleTask.Id} успешно заархивирована в локальный Postgres."));
                }
                else
                {
                    Invoke(() => LogToUi("♻️ [Архив] Обнаружен готовый текст статьи. Пропускаю вызов Текстового ИИ."));
                }

                // 3. УМНЫЙ ГРАФИЧЕСКИЙ КЭШ: Если картинка еще не создавалась — дергаем Stable Diffusion
                if (string.IsNullOrEmpty(articleTask.MongoImageId))
                {
                    Invoke(() => LogToUi("🎨 [Локальный ИИ] Создаю уникальную нейроиллюстрацию в Stable Diffusion..."));
                    try
                    {
                        string base64Image = await _aiService.GenerateImageAsync(articleTask.Topic);
                        if (!string.IsNullOrEmpty(base64Image))
                        {
                            imageBytes = Convert.FromBase64String(base64Image);

                            // Сохраняем обложку в MongoDB GridFS через ваш реальный метод SaveFileAsync
                            string mongoId = await _mongoDbStorageService.SaveFileAsync(imageBytes, $"{articleTask.Id}_cover.jpg", articleTask.Category ?? "");
                            articleTask.MongoImageId = mongoId;

                            using (var dbContext = new AppDbContext())
                            {
                                dbContext.ArticlesQueue.Update(articleTask);
                                await dbContext.SaveChangesAsync();
                            }
                            Invoke(() => LogToUi("💾 [Медиа-Архив] Обложка успешно сохранена в MongoDB GridFS."));
                        }
                    }
                    catch (Exception imgEx)
                    {
                        Invoke(() => LogToUi("⚠️ Сбой обработки картинки (пропускаю): " + imgEx.Message));
                    }
                }
                else
                {
                    Invoke(() => LogToUi("♻️ [Архив] Обложка этой статьи уже есть в MongoDB. Выкачиваю байты для рассылки..."));
                    // Выкачиваем оригинальные байты картинки из GridFS для текущей публикации
                    imageBytes = await _mongoDbStorageService.GetFileAsync(articleTask.MongoImageId);
                }

                // 4. АДРЕСНАЯ ПУБЛИКАЦИЯ: Вместо цикла foreach отправляем строго на целевую платформу подзадачи
                bool isSuccess = false;
                string executionError = string.Empty;

                try
                {
                    IPublisherService? targetPublisher = null;

                    // Определяем, какой именно сервис из полиморфного списка нам нужен
                    if (pubTask.Platform.Equals("WordPress", StringComparison.OrdinalIgnoreCase))
                    {
                        targetPublisher = _publishers.Find(p => p is WordPressPublisherService);
                    }
                    else if (pubTask.Platform.Equals("VK", StringComparison.OrdinalIgnoreCase))
                    {
                        targetPublisher = _publishers.Find(p => p is VkPublisherService);
                    }

                    if (targetPublisher != null)
                    {
                        Invoke(() => LogToUi($"🚀 Отправляю публикацию в [{pubTask.Platform}]..."));
                        isSuccess = await targetPublisher.PublishAsync(articleTask.Topic, articleHtml, articleTask.Category, articleTask.SiteId, imageBytes);
                    }
                    else
                    {
                        throw new Exception($"Не найден зарегистрированный сервис для платформы {pubTask.Platform}");
                    }
                }
                catch (Exception pubEx)
                {
                    executionError = pubEx.Message;

                    // Контролируемый вывод всплывающего окна без падения фонового потока
                    this.BeginInvoke(new Action(() =>
                    {
                        LogToUi($"⚠️ Сбой публикации на платформу [{pubTask.Platform}]: " + pubEx.Message);
                        MessageBox.Show(
                            $"Сбой публикации на платформу [{pubTask.Platform}]:\n\n{pubEx.Message}",
                            "Информация конвейера подзадач",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning
                        );
                    }));
                }

                // 5. ФИКСИРУЕМ РЕЗУЛЬТАТ: Обновляем статус строго для текущей подзадачи
                using (var dbContext = new AppDbContext())
                {
                    pubTask.Status = isSuccess ? Domain.Enums.TaskStatus.Published : Domain.Enums.TaskStatus.Failed;
                    pubTask.ProcessedAt = DateTime.UtcNow;
                    pubTask.ErrorMessage = isSuccess ? null : executionError;

                    dbContext.PublicationTasks.Update(pubTask);
                    await dbContext.SaveChangesAsync();
                }

                Invoke(() => LogToUi(isSuccess
                    ? $"✅ Подзадача #{pubTask.Id} [{pubTask.Platform}] успешно завершена!"
                    : $"❌ Подзадача #{pubTask.Id} [{pubTask.Platform}] провалена. Ошибка занесена в лог базы."));

                // Обновляем визуальную сетку DataGridView
                Invoke(RefreshGrid);
            }
            catch (Exception ex)
            {
                Invoke(() => LogToUi($"💥 Критическая ошибка цикла конвейера: {ex.Message}"));
            }

            // Небольшая технологическая пауза перед следующей подзадачей
            await Task.Delay(5000, cancellationToken);
        }
    }


    private async void RefreshGrid()
    {
        try
        {
            using var db = new AppDbContext();

            // Загружаем только чистые метаданные статей, без привязки к статусам веера
            var articles = await db.ArticlesQueue
                .Select(a => new
                {
                    a.Id,
                    Тема = a.Topic,
                    Рубрика = a.Category,
                    Сайт = a.SiteId,
                    Создано = a.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
                })
                .OrderByDescending(a => a.Id)
                .ToListAsync();

            // Безопасно обновляем DataSource в главном потоке формы
            this.BeginInvoke(new Action(() =>
            {
                _taskGrid.DataSource = articles;

                // Триггерим обновление нижней панели для первой выделившейся строки
                DgvTasks_SelectionChanged(null, EventArgs.Empty);
            }));
        }
        catch (Exception ex)
        {
            this.BeginInvoke(new Action(() => LogToUi($"💥 Ошибка обновления таблицы статей: {ex.Message}")));
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
    private void BtnAddTask_Click(object sender, EventArgs e)
    {
        var platforms = _publishers.Select(p => p.PlatformName);
        using var addForm = new AddTaskForm(platforms);
        if (addForm.ShowDialog(this) == DialogResult.OK)
        {
            LogToUi("✏️ [Очередь] В базу данных вручную добавлена новая задача.");
            RefreshGrid();
        }
    }

    private async void DgvTasks_SelectionChanged(object? sender, EventArgs e)
    {
        // Если в главной таблице ничего не выбрано, очищаем нижнюю панель
        if (_taskGrid.CurrentRow == null)
        {
            dgvDetails.DataSource = null;
            return;
        }

        try
        {
            // Вытаскиваем числовой Id выбранной статьи из первой ячейки строки
            if (_taskGrid.CurrentRow.Cells[0].Value is int articleId)
            {
                using var db = new AppDbContext();

                // Выбираем подзадачи для текущей статьи строго по нашей snake_case структуре
                var detailsData = await db.PublicationTasks
                    .Where(p => p.ArticleTaskId == articleId)
                    .Select(p => new
                    {
                        Платформа = p.Platform,
                        Статус = p.Status.ToString(),
                        Обработано = p.ProcessedAt.HasValue ? p.ProcessedAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "—",
                        Ошибка = p.ErrorMessage ?? "Ошибок нет"
                    })
                    .ToListAsync();

                // Выводим данные в нижний грид
                dgvDetails.DataSource = detailsData;
            }
        }
        catch (Exception ex)
        {
            // Используем родное логирование формы
            LogToUi($"⚠️ Ошибка загрузки детализации веера: {ex.Message}");
        }
    }

}
