using AiSiteFiller.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiSiteFiller.Infrastructure.Services;

public class OkPublisherService : IPublisherService
{
    private readonly string _accessToken, _groupId;
    private readonly ILogger<OkPublisherService> _logger;
    public string PlatformName => "OK";

    // Инициализация, проверка настроек и парсер таблиц из VkPublisherService
    public OkPublisherService(IConfiguration configuration, ILogger<OkPublisherService> logger)
    {
        _logger = logger;
        _accessToken = (configuration["OkOptions:AccessToken"] ?? "").Replace("\n", "").Trim();
        _groupId = (configuration["OkOptions:GroupId"] ?? "").Replace("\n", "").Trim();
    }

    public async Task<bool> PublishAsync(string title, string contentHtml, string metadata, string siteId, byte[]? imageBytes)
    {
        if (string.IsNullOrEmpty(_accessToken) || string.IsNullOrEmpty(_groupId)) return false;

        string textWithFormattedTables = contentHtml;
        try
        {
            // Используем Regex для поиска таблиц, строк и ячеек
            var tableMatches = Regex.Matches(textWithFormattedTables, @"<table[^>]*>(.*?)<\/table>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match tableMatch in tableMatches)
            {
                var rows = Regex.Matches(tableMatch.Groups[1].Value, @"<tr[^>]*>(.*?)<\/tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (rows.Count <= 1) continue;

                var sbTable = new StringBuilder();
                sbTable.AppendLine("\n 📊 СРАВНИТЕЛЬНЫЕ ХАРАКТЕРИСТИКИ МОДЕЛЕЙ:");
                sbTable.AppendLine("───────────────────────────────────");

                // Обработка заголовков
                var headers = Regex.Matches(rows[0].Value, @"<th[^>]*>(.*?)<\/th>|<td[^>]*>(.*?)<\/td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                                   .Cast<Match>()
                                   .Select(m => Regex.Replace(m.Value, @"<[^>]*>", "").Trim()).ToList();

                // Обработка строк данных
                for (int i = 1; i < rows.Count; i++)
                {
                    var cells = Regex.Matches(rows[i].Value, @"<td[^>]*>(.*?)<\/td>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (cells.Count == 0) continue;

                    sbTable.AppendLine("🔹 " + Regex.Replace(cells[0].Value, @"<[^>]*>", "").Trim().ToUpper());
                    for (int j = 1; j < cells.Count && j < headers.Count; j++)
                        sbTable.AppendLine(" ▪ " + headers[j] + ": " + Regex.Replace(cells[j].Value, @"<[^>]*>", "").Trim());
                    sbTable.AppendLine();
                }
                sbTable.AppendLine("───────────────────────────────────");
                textWithFormattedTables = textWithFormattedTables.Replace(tableMatch.Value, sbTable.ToString());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[OK] Ошибка конвертации таблицы: " + ex.Message); //
        }

        // 2. Очистка HTML, формирование маскированной CPA-ссылки
        string cleanText = Regex.Replace(textWithFormattedTables.Replace("<p>", "\n\n").Replace("<br>", "\n"), @"<[^>]*>", "");
        string maskedCpaUrl = Application.Helpers.CpaLinkHelper.GenerateMaskedVkLink(title, "tech-info");

        string finalPostText = $"📰 {title.ToUpper()}\n\n{cleanText}\n\n🚀 Подробности: {maskedCpaUrl}";

        // 3. Загрузка фото в ОК (Graph API)
        string photoToken = "";
        if (imageBytes?.Length > 0)
        {
            using var client = new HttpClient();

            // ЖЕЛЕЗНЫЙ ФИКС: Экранируем токен, чтобы двоеточие внутри него не ломало парсер портов HttpClient
            string escapedToken = Uri.EscapeDataString(_accessToken);
            string uploadServerUrl = "https://" + "api." + "ok." + "ru/" + "graph/" + "me/" + "photos" + "?access_token=" + escapedToken;

            var serverResponse = await client.PostAsync(uploadServerUrl, null);
            var serverResponseStr = await serverResponse.Content.ReadAsStringAsync();
            var serverData = JsonDocument.Parse(serverResponseStr);

            if (serverData.RootElement.TryGetProperty("uploadUrl", out var urlEl))
            {
                // Шаг Б: Загрузка файла
                using var content = new MultipartFormDataContent { { new ByteArrayContent(imageBytes), "file", "img.jpg" } };
                var uploadResponse = await client.PostAsync(urlEl.GetString(), content);
                var uploadData = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync());

                if (uploadData.RootElement.TryGetProperty("token", out var tokenEl))
                    photoToken = tokenEl.GetString();
            }
        }

        // 4. Опубликовать медиатопик на стену группы
        using var finalClient = new HttpClient();
        string escapedTokenFinal = Uri.EscapeDataString(_accessToken);
        string requestUrl = "https://" + "api." + "ok." + "ru/" + "graph/" + "me/" + "posts" + "?access_token=" + escapedTokenFinal;


        // Формируем JSON-структуру поста. Одноклассники принимают массив attachments
        var postData = new
        {
            message = finalPostText,
            attachments = photoToken != "" ? new object[] { new { type = "photo", token = photoToken } } : Array.Empty<object>()
        };

        var jsonContent = new StringContent(JsonSerializer.Serialize(postData), Encoding.UTF8, "application/json");
        var response = await finalClient.PostAsync(requestUrl, jsonContent);
        string resultStr = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception("Ошибка OK Graph API: " + resultStr);
        }

        _logger.LogInformation("✅ [OK] Пост с обложкой успешно опубликован в группу " + _groupId + "!");
        return true;
    }
}
