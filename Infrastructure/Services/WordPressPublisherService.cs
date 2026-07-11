using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AiSiteFiller.Application.Interfaces;

namespace AiSiteFiller.Infrastructure.Services;

public class WordPressPublisherService : IPublisherService
{
    private readonly ILogger<WordPressPublisherService> _logger;
    private readonly IConfiguration _configuration;

    public WordPressPublisherService(IConfiguration configuration, ILogger<WordPressPublisherService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    // Обновленный метод интерфейса (мы добавим siteId в аргументы интерфейса на Шаге 4)
    public async Task<bool> PublishAsync(string title, string contentHtml, string category, string siteId)
    {
        _logger.LogInformation($"[WordPress] Ищу настройки для сайта: '{siteId}'...");

        // 1. Динамически считываем массив сайтов из конфигурации
        var sites = _configuration.GetSection("WordPressSites").Get<List<WordPressSiteConfig>>();
        var currentSite = sites?.Find(s => s.Id == siteId);

        if (currentSite == null)
        {
            _logger.LogError($"❌ Настройки для сайта с Id='{siteId}' не найдены в appsettings.json!");
            return false;
        }

        // 2. Вычищаем данные конкретного выбранного сайта
        string cleanUrl = Regex.Replace(currentSite.BaseUrl ?? string.Empty, @"[^a-zA-Z0-9\.\:\/\-]", "").Trim();
        string cleanUsername = Regex.Replace(currentSite.Username ?? string.Empty, @"[^a-zA-Z0-9_\-\.\@]", "").Trim();
        string cleanPassword = Regex.Replace(currentSite.AppPassword ?? string.Empty, @"[^a-zA-Z0-9 ]", "").Trim();

        if (!cleanUrl.StartsWith("http://") && !cleanUrl.StartsWith("https://"))
        {
            cleanUrl = "http://" + cleanUrl;
        }

        // 3. Собираем изолированный HttpClient под этот конкретный сайт
        var handler = new HttpClientHandler { UseDefaultCredentials = false, UseProxy = false };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri(cleanUrl) };

        string rawCredentials = $"{cleanUsername}:{cleanPassword}";
        string authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes(rawCredentials));

        // 4. Формируем JSON-тело поста
        var wpId = AiSiteFiller.Domain.Constants.AppCategories.GetWordPressId(category);
        var postData = new WordPressPostRequest
        {
            Title = title,
            Content = contentHtml,
            Status = "publish",
            Categories = wpId.HasValue ? new int[] { wpId.Value } : Array.Empty<int>()
        };

        string jsonBody = JsonSerializer.Serialize(postData, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        // 5. Собираем сетевой пакет
        var request = new HttpRequestMessage(HttpMethod.Post, "/wp-json/wp/v2/posts");
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {authToken}");
        request.Headers.TryAddWithoutValidation("Connection", "close");

        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(jsonBody));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Content = content;

        try
        {
            HttpResponseMessage response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            string errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError($"❌ Сервер {cleanUrl} отклонил запрос. Код: {response.StatusCode}, Текст: {errorContent}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"❌ Сетевое исключение при отправке на сайт {cleanUrl}");
            return false;
        }
    }

    // Класс-маппер для чтения структуры из JSON массива
    private class WordPressSiteConfig
    {
        public string Id { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string AppPassword { get; set; } = string.Empty;
    }

    private class WordPressPostRequest
    {
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = "draft";
        [JsonPropertyName("categories")] public int[] Categories { get; set; } = Array.Empty<int>();
    }
}
