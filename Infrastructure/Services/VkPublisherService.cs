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
            // Форматируем текст анонса (берём первый абзац)
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

            // В URI отправляем только служебные данные
            string requestUri = "https://vk.com" +
                                "?v=5.131" +
                                "&access_token=" + cleanToken;

            // Передаем ТОЛЬКО owner_id и message. 
            // Для токена самой группы этого более чем достаточно, чтобы выпустить пост от имени паблика
            string postDataBody = "owner_id=-" + cleanGroup.Replace("-", "").Trim() +
                                  "&message=" + Uri.EscapeDataString(finalPostText);


            var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var isolatedClient = new HttpClient(handler);
            isolatedClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            // Используем StringContent вместо капризного FormUrlEncodedContent
            var content = new StringContent(postDataBody, Encoding.UTF8, "application/x-www-form-urlencoded");

            // НАМЕРТВО БЛОКИРУЕМ CHUNKED-ПЕРЕДАЧУ .NET 8.0: Явно задаем длину контента в байтах
            byte[] bodyBytes = Encoding.UTF8.GetBytes(postDataBody);
            content.Headers.ContentLength = bodyBytes.Length;

            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(requestUri)) { Content = content };
            var wallResponse = await isolatedClient.SendAsync(request);

            byte[] responseBytes = await wallResponse.Content.ReadAsByteArrayAsync();
            string wallResultString = Encoding.UTF8.GetString(responseBytes).Trim();

            // Срезаем BOM-маркеры ВК
            wallResultString = wallResultString.Trim(new char[] { '\uFEFF', '\u200B', ' ', '\n', '\r', '\t' });

            // Оставляем дамп-лог для истории
            string debugFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vk_error_page.html");
            await System.IO.File.WriteAllTextAsync(debugFilePath, wallResultString, Encoding.UTF8);

            if (wallResultString.StartsWith("<"))
            {
                throw new Exception("ВК вернул HTML-страницу вместо JSON-ответа.");
            }

            using var wallDoc = JsonDocument.Parse(wallResultString);

            if (wallDoc.RootElement.TryGetProperty("error", out var wallErrorEl))
            {
                string errMsg = wallErrorEl.GetProperty("error_msg").GetString() ?? "Unknown Error";
                int errCode = wallErrorEl.GetProperty("error_code").GetInt32();

                // ПРОБРАСЫВАЕМ ПОДРОБНЫЙ ТЕКСТ ДЛЯ ВСПЛЫВАШКИ СЮДА:
                throw new Exception($"[Код {errCode}]: {errMsg}\n\nПолный ответ сервера:\n{wallResultString}");
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
