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

        _logger.LogInformation("[VK] Публикую иллюстрированный пост на стену через гибридный POST-запрос...");

        try
        {
            // Форматируем текст поста
            string cleanText = contentHtml
                .Replace("<p>", "").Replace("</p>", "\n\n")
                .Replace("<h2>", "🔥 ").Replace("</h2>", " \n")
                .Replace("<h3>", "⚡ ").Replace("</h3>", " \n")
                .Replace("<ul>", "").Replace("</ul>", "")
                .Replace("<li>", "🔹 ").Replace("</li>", "\n")
                .Replace("strong", "").Replace("</strong>", "");

            cleanText = System.Text.RegularExpressions.Regex.Replace(cleanText, @"<[^>]*>", "");

            string finalPostText = "📰 " + title.ToUpper() + "\n\n" +
                                   cleanText + "\n" +
                                   "📌 Читайте этот и другие обзоры гаджетов на нашем сайте: https://mistmare.ru";

            // КРИТИЧЕСКИЙ ФИКС: Токен и Версию отправляем строго в URI
            string requestUri = "https://vk.com" +
                                "?v=5.131" +
                                "&access_token=" + cleanToken;

            // Данные сообщения упаковываем в тело POST-запроса
            var postData = "owner_id=-" + cleanGroup +
                           "&from_group=1" +
                           "&message=" + Uri.EscapeDataString(finalPostText);

            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(requestUri));
            request.Content = new StringContent(postData, Encoding.UTF8, "application/x-www-form-urlencoded");

            // Отключаем прокси и редиректы для изоляции
            var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var singleClient = new HttpClient(handler);
            singleClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            var wallResponse = await singleClient.SendAsync(request);

            byte[] responseBytes = await wallResponse.Content.ReadAsByteArrayAsync();
            string wallResultString = Encoding.UTF8.GetString(responseBytes);

            // Срезаем BOM-маркеры на всякий случай
            wallResultString = wallResultString.Trim(new char[] { '\uFEFF', '\u200B', ' ', '\n', '\r', '\t' });

            // Находим эту проверку в коде:
            if (string.IsNullOrEmpty(wallResultString) || wallResultString.StartsWith("<"))
            {
                // ЖЕЛЕЗНАЯ ДИАГНОСТИКА: Сохраняем всю HTML страницу на ваш диск
                string debugFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vk_error_page.html");
                await System.IO.File.WriteAllTextAsync(debugFilePath, wallResultString ?? "Empty response", Encoding.UTF8);

                throw new Exception("ВК вернул HTML-страницу вместо JSON. Полный код ответа сохранен в файл: " + debugFilePath);
            }

            using var wallDoc = JsonDocument.Parse(wallResultString);

            if (wallDoc.RootElement.TryGetProperty("error", out var wallErrorEl))
            {
                string errMsg = wallErrorEl.GetProperty("error_msg").GetString() ?? "Неизвестная ошибка";
                int errCode = wallErrorEl.GetProperty("error_code").GetInt32();
                throw new Exception("VK API Error (wall.post) [Код " + errCode + "]: " + errMsg);
            }

            _logger.LogInformation("✅ [VK] Пост успешно опубликован на стену группы " + cleanGroup + "!");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("❌ [VK] Ошибка публикации: " + ex.Message);
            throw;
        }
    }
}
