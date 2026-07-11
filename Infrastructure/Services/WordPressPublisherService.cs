using System;
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
    private readonly HttpClient _httpClient;
    private readonly ILogger<WordPressPublisherService> _logger;
    private readonly string _authToken; // Будем хранить токен отдельно, а не в DefaultRequestHeaders

    public WordPressPublisherService(IConfiguration configuration, ILogger<WordPressPublisherService> logger)
    {
        _logger = logger;

        string rawBaseUrl = configuration["WordPressOptions:BaseUrl"] ?? string.Empty;
        string rawUsername = configuration["WordPressOptions:Username"] ?? string.Empty;
        string rawAppPassword = configuration["WordPressOptions:AppPassword"] ?? string.Empty;

        string cleanUrl = Regex.Replace(rawBaseUrl, @"[^a-zA-Z0-9\.\:\/\-]", "").Trim();
        string cleanUsername = Regex.Replace(rawUsername, @"[^a-zA-Z0-9_\-\.\@]", "").Trim();
        string cleanPassword = Regex.Replace(rawAppPassword, @"[^a-zA-Z0-9 ]", "").Trim();

        if (!cleanUrl.StartsWith("http://") && !cleanUrl.StartsWith("https://"))
        {
            cleanUrl = "http://" + cleanUrl;
        }

        // КРИТИЧЕСКИЙ МОМЕНТ: Инициализируем абсолютно чистый HttpClient без системных заголовков Windows
        var handler = new HttpClientHandler { UseDefaultCredentials = false };
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(cleanUrl)
        };

        // Полностью очищаем все заголовки по умолчанию, которые .NET мог подтянуть из ОС
        _httpClient.DefaultRequestHeaders.Clear();

        string rawCredentials = $"{cleanUsername}:{cleanPassword}";
        byte[] credentialBytes = Encoding.ASCII.GetBytes(rawCredentials);
        _authToken = Convert.ToBase64String(credentialBytes);
    }

    public async Task<bool> PublishAsync(string title, string contentHtml, string category)
    {
        _logger.LogInformation("🌐 Отправка статьи в WordPress REST API. Название: \"{Title}\"", title);

        var wpId = AiSiteFiller.Domain.Constants.AppCategories.GetWordPressId(category);

        var postData = new WordPressPostRequest
        {
            Title = title,
            Content = contentHtml,
            Status = "publish",
            Categories = wpId.HasValue ? new int[] { wpId.Value } : Array.Empty<int>()
        };

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string jsonBody = JsonSerializer.Serialize(postData, jsonOptions);

        // Формируем низкоуровневый HttpRequestMessage вручную, чтобы контролировать каждый байт заголовков
        var request = new HttpRequestMessage(HttpMethod.Post, "/wp-json/wp/v2/posts");

        // Добавляем заголовки СТРОГО в ASCII
        request.Headers.Clear();
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {_authToken}");
        request.Headers.TryAddWithoutValidation("Connection", "close");

        var content = new StringContent(jsonBody, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Content = content;

        try
        {
            // Отправляем изолированный запрос
            HttpResponseMessage response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("✅ Статья '{Title}' успешно опубликована на движке WordPress!", title);
                return true;
            }

            string errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("❌ Сервер WordPress отклонил запрос. Код: {Code}, Текст ошибки: {Error}", response.StatusCode, errorContent);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Сетевое исключение при отправке в WordPress для статьи: \"{Title}\"", title);
            return false;
        }
    }

    private class WordPressPostRequest
    {
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = "draft";
        [JsonPropertyName("categories")] public int[] Categories { get; set; } = Array.Empty<int>();
    }
}
