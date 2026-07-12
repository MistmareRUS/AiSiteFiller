using AiSiteFiller.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace AiSiteFiller.Infrastructure.Services;

public class VkPublisherService : IPublisherService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<VkPublisherService> _logger;
    private readonly string _accessToken;
    private readonly string _groupId;

    public VkPublisherService(IConfiguration configuration, ILogger<VkPublisherService> logger)
    {
        _logger = logger;
        _accessToken = (configuration["VkOptions:AccessToken"] ?? string.Empty).Trim();
        _groupId = (configuration["VkOptions:GroupId"] ?? string.Empty).Trim();

        var handler = new HttpClientHandler { UseDefaultCredentials = false, UseProxy = false };
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://vk.com")
        };
    }

    public async Task<bool> PublishAsync(string title, string contentHtml, string metadata, string siteId, byte[]? imageBytes)
    {
        string cleanToken = (_accessToken ?? "").Replace("\n", "").Replace("\r", "").Trim();
        string cleanGroup = (_groupId ?? "").Replace("\n", "").Replace("\r", "").Trim();

        if (string.IsNullOrEmpty(cleanToken) || string.IsNullOrEmpty(cleanGroup))
        {
            _logger.LogWarning("[VK] Настройки VK не заданы в appsettings.local.json. Пропускаю.");
            return false;
        }

        _logger.LogInformation("[VK] Публикую красивый SEO-анонс статьи на стену паблика...");

        try
        {
            // 1. Извлекаем первый абзац текста для анонса
            string cleanText = contentHtml;
            int pStart = cleanText.IndexOf("<p>");
            int pEnd = cleanText.IndexOf("</p>");

            if (pStart >= 0 && pEnd > pStart)
            {
                cleanText = cleanText.Substring(pStart + 3, pEnd - pStart - 3);
            }

            cleanText = System.Text.RegularExpressions.Regex.Replace(cleanText, @"<[^>]*>", "");

            if (cleanText.Length > 300)
            {
                cleanText = cleanText.Substring(0, 300) + "...";
            }

            string finalPostText = "🔥 НОВАЯ СТАТЬЯ: " + title.ToUpper() + "\n\n" +
                                   cleanText + "\n\n" +
                                   "🚀 Читать полный обзор с подробными тестами на нашем сайте:\n" +
                                   "👉 https://mistmare.ru";

            // 2. ИСПРАВЛЕНИЕ АДРЕСА: Четкий, абсолютный URI со всеми слешами
            string requestUri = "https://vk.com" +
                                "?v=5.131" +
                                "&access_token=" + cleanToken;

            // Данные сообщения. Для пабликов важенfrom_group=1
            var postData = new System.Collections.Generic.Dictionary<string, string>
        {
            { "owner_id", "-" + cleanGroup }, // Минус перед ID обязателен для стен сообществ
            { "from_group", "1" },           // Публикация строго от имени сообщества
            { "message", finalPostText }
        };

            var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var isolatedClient = new HttpClient(handler);
            isolatedClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            // Отправляем как стандартную форму
            var content = new FormUrlEncodedContent(postData);
            var wallResponse = await isolatedClient.PostAsync(requestUri, content);

            byte[] responseBytes = await wallResponse.Content.ReadAsByteArrayAsync();
            string wallResultString = Encoding.UTF8.GetString(responseBytes).Trim();
            wallResultString = wallResultString.Trim(new char[] { '\uFEFF', '\u200B', ' ', '\n', '\r', '\t' });

            if (string.IsNullOrEmpty(wallResultString) || wallResultString.StartsWith("<"))
            {
                string debugFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vk_error_page.html");
                await System.IO.File.WriteAllTextAsync(debugFilePath, wallResultString ?? "Empty response", Encoding.UTF8);
                throw new Exception("ВК вернул HTML-страницу. Код ответа сохранен в файл: " + debugFilePath);
            }

            using var wallDoc = JsonDocument.Parse(wallResultString);

            if (wallDoc.RootElement.TryGetProperty("error", out var wallErrorEl))
            {
                string errMsg = wallErrorEl.GetProperty("error_msg").GetString() ?? "Неизвестная ошибка";
                int errCode = wallErrorEl.GetProperty("error_code").GetInt32();
                throw new Exception("VK API Error (wall.post) [Код " + errCode + "]: " + errMsg);
            }

            _logger.LogInformation("✅ [VK] Анонс статьи успешно опубликован на стену группы " + cleanGroup + "!");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("❌ [VK] Ошибка публикации: " + ex.Message);
            throw;
        }
    }

}
