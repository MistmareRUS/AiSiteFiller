using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AiSiteFiller.Application.Interfaces;

namespace AiSiteFiller.Infrastructure.Services;

public class VkPublisherService : IPublisherService
{
    private readonly string _accessToken;
    private readonly string _groupId;
    private readonly ILogger<VkPublisherService> _logger;

    public VkPublisherService(IConfiguration configuration, ILogger<VkPublisherService> logger)
    {
        _logger = logger;
        _accessToken = (configuration["VkOptions:AccessToken"] ?? string.Empty).Replace("\n", "").Replace("\r", "").Trim();
        _groupId = (configuration["VkOptions:GroupId"] ?? string.Empty).Replace("\n", "").Replace("\r", "").Trim();
    }

    public async Task<bool> PublishAsync(string title, string contentHtml, string metadata, string siteId, byte[]? imageBytes)
    {
        string cleanToken = _accessToken;
        string cleanGroup = _groupId;

        if (string.IsNullOrEmpty(cleanToken) || string.IsNullOrEmpty(cleanGroup))
        {
            _logger.LogWarning("[VK] Настройки VK не заданы в appsettings.local.json. Пропускаю.");
            return false;
        }

        _logger.LogInformation("[VK] Публикую красивый SEO-анонс статьи на стену паблика...");

        try
        {
            // Извлекаем первый абзац текста для анонса
            string cleanText = contentHtml;
            int pStart = cleanText.IndexOf("<p>");
            int pEnd = cleanText.IndexOf("</p>");

            if (pStart >= 0 && pEnd > pStart)
            {
                cleanText = cleanText.Substring(pStart + 3, pEnd - pStart - 3);
            }

            cleanText = System.Text.RegularExpressions.Regex.Replace(cleanText, @"<[^>]*>", "");

            if (cleanText.Length > 350)
            {
                cleanText = cleanText.Substring(0, 350) + "...";
            }

            string finalPostText = "🔥 НОВАЯ СТАТЬЯ: " + title.ToUpper() + "\n\n" +
                                   cleanText + "\n\n" +
                                   "🚀 Читать полный обзор с подробными тестами на нашем сайте:\n" +
                                   "👉 https://mistmare.ru";

            // Собираем ВСЕ параметры строго в URI для атомарности запроса
            string requestUri = "https://vk.com" +
                                "?v=5.131" +
                                "&owner_id=-" + cleanGroup +
                                "&from_group=1" +
                                "&message=" + Uri.EscapeDataString(finalPostText) +
                                "&access_token=" + cleanToken;

            var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var isolatedClient = new HttpClient(handler);
            isolatedClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            // Отправляем пустой POST-запрос
            var wallResponse = await isolatedClient.PostAsync(requestUri, null);

            byte[] responseBytes = await wallResponse.Content.ReadAsByteArrayAsync();
            string wallResultString = Encoding.UTF8.GetString(responseBytes).Trim();
            wallResultString = wallResultString.Trim(new char[] { '\uFEFF', '\u200B', ' ', '\n', '\r', '\t' });

            // Запись дампа для подстраховки
            string debugFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vk_error_page.html");
            await System.IO.File.WriteAllTextAsync(debugFilePath, wallResultString, Encoding.UTF8);

            if (wallResultString.StartsWith("<"))
            {
                throw new Exception("ВК вернул HTML-страницу веб-сайта вместо программного JSON-ответа.");
            }

            using var wallDoc = JsonDocument.Parse(wallResultString);

            if (wallDoc.RootElement.TryGetProperty("error", out var wallErrorEl))
            {
                string errMsg = wallErrorEl.GetProperty("error_msg").GetString() ?? "Unknown Error";
                int errCode = wallErrorEl.GetProperty("error_code").GetInt32();
                throw new Exception("VK API Error [Код " + errCode + "]: " + errMsg);
            }

            if (wallDoc.RootElement.TryGetProperty("response", out var responseEl))
            {
                _logger.LogInformation("✅ [VK] Анонс статьи успешно опубликован на стену паблика " + cleanGroup + "!");
                return true;
            }

            throw new Exception("Неизвестный формат ответа от ВКонтакте: " + wallResultString);
        }
        catch (Exception ex)
        {
            _logger.LogError("❌ [VK] Ошибка публикации: " + ex.Message);
            throw;
        }
    }
}
