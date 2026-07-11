using System;
using System.Drawing;
using System.Windows.Forms;
using AiSiteFiller.Infrastructure.Data;

namespace AiSiteFiller;

public class ArticleViewerForm : Form
{
    private readonly int _taskId;
    private WebBrowser _webBrowser = null!;

    public ArticleViewerForm(int taskId, string topic)
    {
        _taskId = taskId;
        this.Text = "Просмотр статьи: " + topic;
        this.Size = new Size(800, 600);
        this.StartPosition = FormStartPosition.CenterParent;

        InitializeComponent();
        LoadArticleContent();
    }

    private void InitializeComponent()
    {
        // Создаем встроенный движок браузера на всю форму
        _webBrowser = new WebBrowser
        {
            Dock = DockStyle.Fill,
            AllowWebBrowserDrop = false,
            IsWebBrowserContextMenuEnabled = true,
            WebBrowserShortcutsEnabled = true
        };

        this.Controls.Add(_webBrowser);
    }

    private void LoadArticleContent()
    {
        try
        {
            using var db = new AppDbContext();
            var task = db.ArticlesQueue.Find(_taskId);

            if (task == null)
            {
                _webBrowser.DocumentText = "<h3>❌ Ошибка: Задача не найдена в базе данных.</h3>";
                return;
            }

            if (string.IsNullOrEmpty(task.ContentHtml))
            {
                _webBrowser.DocumentText = "<h3>ℹ️ Текст для этой статьи еще не сгенерирован (статус: " + task.Status + ").</h3>";
                return;
            }

            // Накатываем простую стилизацию шрифтов, чтобы HTML в окошке выглядел красиво и современно
            string beautifulHtml = "<html><head><style>" +
                                 "body { font-family: 'Segoe UI', sans-serif; padding: 20px; line-height: 1.6; color: #333; }" +
                                 "h2 { color: #2c3e50; border-bottom: 2px solid #ecf0f1; padding-bottom: 8px; }" +
                                 "h3 { color: #34495e; }" +
                                 "table { border-collapse: collapse; width: 100%; margin: 20px 0; }" +
                                 "table, th, td { border: 1px solid #bdc3c7; padding: 10px; text-align: left; }" +
                                 "th { background-color: #f2f2f2; }" +
                                 "strong { color: #2c3e50; }" +
                                 "</style></head><body>" + task.ContentHtml + "</body></html>";

            // Загружаем и рендерим HTML текст прямо в окне ПК
            _webBrowser.DocumentText = beautifulHtml;
        }
        catch (Exception ex)
        {
            _webBrowser.DocumentText = "<h3>❌ Ошибка загрузки из Postgres: " + ex.Message + "</h3>";
        }
    }
}
