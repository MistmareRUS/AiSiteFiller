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

        _logger.LogInformation("[VK] Публикую красивый иллюстрированный пост на стену через чистый автономный клиент...");

        try
        {
            // 1. Форматируем HTML текст в красивый пост для соцсетей
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

            // 2. Формируем точный URL: токен и версию отправляем строго в адресной строке
            string fullUrl = "https://vk.com" +
                              "?v=5.131" +
                              "&access_token=" + cleanToken;

            // Данные сообщения и ID группы переводим в чистую строку без использования FormUrlEncodedContent
            var postDataString = "owner_id=-" + cleanGroup +
                                 "&from_group=1" +
                                 "&message=" + Uri.EscapeDataString(finalPostText);

            // Инициализируем полностью изолированный клиент БЕЗ BaseAddress
            var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var isolatedClient = new HttpClient(handler);

            // Маскируем запрос под обычный браузер
            isolatedClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var content = new StringContent(postDataString, Encoding.UTF8, "application/x-www-form-urlencoded");

            // КРИТИЧЕСКИЙ ФИКС: Принудительно вычисляем длину контента в байтах.
            // Наличие заголовка Content-Length автоматически отключает chunked-кодирование в .NET 8!
            byte[] postBytes = Encoding.UTF8.GetBytes(postDataString);
            content.Headers.ContentLength = postBytes.Length;

            // Создаем сам запрос
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(fullUrl))
            {
                Content = content
            };

            // Отправляем запрос через изолированный клиент
            var wallResponse = await isolatedClient.SendAsync(request);

            // Читаем ответ как массив байт для обхода проблем с BOM и кодировками ВК
            byte[] responseBytes = await wallResponse.Content.ReadAsByteArrayAsync();
            string wallResultString = Encoding.UTF8.GetString(responseBytes).Trim();

            // 3. ЖЕСТКАЯ ДИАГНОСТИКА: Если ВК вернул HTML — пишем его весь на диск для анализа
            if (string.IsNullOrEmpty(wallResultString) || wallResultString.StartsWith("<"))
            {
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
