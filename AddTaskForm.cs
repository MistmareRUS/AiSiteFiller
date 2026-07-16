using AiSiteFiller.Domain.Entities;
using AiSiteFiller.Infrastructure.Data;

namespace AiSiteFiller;

public class AddTaskForm : Form
{
    private TextBox _txtTopic = null!;
    private ComboBox _cmbCategory = null!;
    private ComboBox _cmbSite = null!;
    private Button _btnSave = null!;
    private Button _btnCancel = null!;

    private readonly System.Collections.Generic.IEnumerable<string> _availablePlatforms;

    // Модифицируем конструктор для принятия списка платформ
    public AddTaskForm(System.Collections.Generic.IEnumerable<string> availablePlatforms)
    {
        _availablePlatforms = availablePlatforms;
        InitializeComponent();
    }


    private void InitializeComponent()
    {
        this.Text = "Добавление новой задачи в очередь";
        this.Size = new Size(500, 320);
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.StartPosition = FormStartPosition.CenterParent;
        this.MaximizeBox = false;
        this.MinimizeBox = false;

        // Поле ввода темы
        Label lblTopic = new Label { Text = "Тема (название) статьи:", Location = new Point(20, 20), AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        _txtTopic = new TextBox { Location = new Point(20, 45), Size = new Size(440, 25) };

        // Выбор категории
        Label lblCategory = new Label { Text = "Рубрика (Категория):", Location = new Point(20, 90), AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        _cmbCategory = new ComboBox { Location = new Point(20, 115), Size = new Size(200, 25), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbCategory.Items.AddRange(new object[] { "smart-home", "smartphones", "audio", "gadgets" });
        _cmbCategory.SelectedIndex = 0;

        // Выбор целевой платформы (Сайт/ВК)
        Label lblSite = new Label { Text = "Сайт-первоисточник (SiteId):", Location = new Point(260, 90), AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        _cmbSite = new ComboBox { Location = new Point(260, 115), Size = new Size(200, 25), DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbSite.Items.AddRange(new object[] { "tech-info", "vk-group" });
        _cmbSite.SelectedIndex = 0;

        // Кнопки управления
        _btnSave = new Button { Text = "💾 Сохранить в БД", Location = new Point(180, 200), Size = new Size(130, 35), BackColor = Color.LightGreen, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        _btnSave.Click += BtnSave_Click;

        _btnCancel = new Button { Text = "Отмена", Location = new Point(330, 200), Size = new Size(130, 35), BackColor = Color.LightGray };
        _btnCancel.Click += (s, e) => this.Close();

        this.Controls.Add(lblTopic);
        this.Controls.Add(_txtTopic);
        this.Controls.Add(lblCategory);
        this.Controls.Add(_cmbCategory);
        this.Controls.Add(lblSite);
        this.Controls.Add(_cmbSite);
        this.Controls.Add(_btnSave);
        this.Controls.Add(_btnCancel);
    }

    private async void BtnSave_Click(object? sender, EventArgs e)
    {
        string topicText = _txtTopic.Text.Trim();
        if (string.IsNullOrEmpty(topicText) || topicText.Length < 10)
        {
            MessageBox.Show("Пожалуйста, введите корректное название темы (не менее 10 символов).", "Предупреждение", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            using var db = new AppDbContext();

            // 1. Сначала сохраняем саму статью, чтобы Postgres сгенерировал её Id
            var newTask = new ArticleTask
            {
                Topic = topicText,
                Category = _cmbCategory.SelectedItem?.ToString() ?? "gadgets",
                SiteId = _cmbSite.SelectedItem?.ToString() ?? "tech-info",
                Status = Domain.Enums.TaskStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            await db.ArticlesQueue.AddAsync(newTask);
            await db.SaveChangesAsync(); // Фиксируем статью в БД

            // 2. АВТОМАТИЧЕСКИЙ ВЕЕР: Создаем две связанные подзадачи для публикации в snake_case таблицу
            foreach (var platformName in _availablePlatforms)
            {
                await db.PublicationTasks.AddAsync(new PublicationTask
                {
                    ArticleTaskId = newTask.Id,
                    Platform = platformName, // Авто-подстановка "WordPress", "VK" и т.д.
                    Status = Domain.Enums.TaskStatus.Pending
                });
            }
            await db.SaveChangesAsync();
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Ошибка записи в PostgreSQL: " + ex.Message, "Критическая ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
