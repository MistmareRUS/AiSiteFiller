using AiSiteFiller.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiSiteFiller.Infrastructure.Services;

public class OpenAiGptService : IAiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiGptService> _logger;
    private readonly string _apiKey;
    private readonly string _absoluteUrl; // Будем хранить готовый абсолютный URL-адрес

    public OpenAiGptService(IConfiguration configuration, ILogger<OpenAiGptService> logger)
    {
        _logger = logger;

        string rawApiKey = configuration["OpenAiOptions:ApiKey"] ?? string.Empty;
        string rawBaseUrl = configuration["OpenAiOptions:BaseUrl"] ?? string.Empty;

        if (string.IsNullOrEmpty(rawApiKey))
        {
            _logger.LogCritical("OpenAI ApiKey не задан в appsettings.json!");
            throw new ArgumentNullException(nameof(rawApiKey));
        }

        // ЖЕСТКАЯ ВЫЧИСТКА: Выжигаем любые не-ASCII символы, пробелы и невидимый мусор из ключа и URL
        _apiKey = Regex.Replace(rawApiKey, @"[^a-zA-Z0-9_\-\+]", "").Trim();
        string cleanBaseUrl = Regex.Replace(rawBaseUrl, @"[^a-zA-Z0-9\.\:\/\-]", "").Trim();

        if (string.IsNullOrEmpty(cleanBaseUrl))
        {
            cleanBaseUrl = "https://openai.com";
        }

        if (!cleanBaseUrl.EndsWith("/"))
        {
            cleanBaseUrl += "/";
        }

        // Собираем кристально чистый абсолютный путь для запросов
        _absoluteUrl = cleanBaseUrl + "chat/completions";

        // Настраиваем хендлер: отключаем прокси, системные креды и ОПАСНЫЕ автоматические редиректы
        var handler = new HttpClientHandler
        {
            UseDefaultCredentials = false,
            UseProxy = false,
            AllowAutoRedirect = false // ◄── Запрещаем скрытые прыжки по адресам, вызывающие ошибку
        };

        _httpClient = new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.Clear();
    }

    public async Task<string> GenerateArticleAsync(string topic)
    {
        _logger.LogInformation("[ИИ] Формирую ТЗ для статьи...");

        string prompt = $@"
        Напиши подробную, экспертную SEO-оптимизированную статью на тему: ""{topic}"".
        Используй реалии текущего 2026 года.
        
        Требования к тексту:
        1. Напиши статью в формате HTML (используй теги <h2>, <h3>, <p>, <ul>, <li>, <strong>). Не используй обертки ```html или теги <html>, <head>, <body>.
        2. Структура: Введение, детальный разбор (3-4 подзаголовка),超 блок с плюсами и минусами, краткий вывод.
        3. Добавь в текст одну логическую таблицу (тег <table>) с ключевыми характеристиками или сравнением.
        4. Тон автора: дружелюбный, экспертный, без «воды» и банальных фраз. Пиши для людей.
        5. Объем: не менее 4000 знаков.";

        var requestBody = new OpenAiChatRequest
        {
            Model = "gpt-4o-mini",
            Temperature = 0.7f,
            Messages = new[]
            {
                new OpenAiMessage { Role = "system", Content = "Ты professional SEO copywriter." },
                new OpenAiMessage { Role = "user", Content = prompt }
            }
        };

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string jsonString = JsonSerializer.Serialize(requestBody, jsonOptions);

        // КРИТИЧЕСКИЙ ФИКС: Передаем СТРОГО абсолютный, очищенный URI без относительных путей
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_absoluteUrl));

        request.Headers.Clear();
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        request.Headers.TryAddWithoutValidation("Connection", "close");

        byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonString);
        var content = new ByteArrayContent(jsonBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Content = content;

        try
        {
            HttpResponseMessage response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<OpenAiChatResponse>(responseString, jsonOptions);

                if (result?.Choices != null && result.Choices.Length > 0)
                {
                    string? generatedText = result.Choices[0].Message?.Content;
                    if (!string.IsNullOrEmpty(generatedText))
                    {
                        return generatedText;
                    }
                }
            }

            string errorBody = await response.Content.ReadAsStringAsync();
            throw new Exception($"OpenAI API returned error ({response.StatusCode}): {errorBody}");
        }
        catch (Exception ex)
        {
            _logger.LogError("❌ Исключение при работе с OpenAI API.");
            throw;
        }
    }

    private class OpenAiChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "gpt-4o-mini";
        [JsonPropertyName("messages")] public OpenAiMessage[] Messages { get; set; } = Array.Empty<OpenAiMessage>();
        [JsonPropertyName("temperature")] public float Temperature { get; set; } = 0.7f;
    }

    private class OpenAiMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }

    private class OpenAiChatResponse
    {
        [JsonPropertyName("choices")] public Choice[]? Choices { get; set; }
    }

    private class Choice
    {
        [JsonPropertyName("message")] public OpenAiMessage? Message { get; set; }
    }
}
