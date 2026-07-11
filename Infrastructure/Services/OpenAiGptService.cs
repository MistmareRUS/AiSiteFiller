using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AiSiteFiller.Application.Interfaces;

namespace AiSiteFiller.Infrastructure.Services;

public class OpenAiGptService : IAiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiGptService> _logger;
    private readonly string _apiKey;

    public OpenAiGptService(IConfiguration configuration, ILogger<OpenAiGptService> logger)
    {
        _logger = logger;

        string? apiKey = configuration["OpenAiOptions:ApiKey"];
        string? baseUrl = configuration["OpenAiOptions:BaseUrl"];

        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogCritical("OpenAI ApiKey не задан в appsettings.json! Работа сервиса ИИ невозможна.");
            throw new ArgumentNullException(nameof(apiKey));
        }

        _apiKey = apiKey.Trim();

        if (string.IsNullOrEmpty(baseUrl))
        {
            baseUrl = "https://openai.com";
        }

        // Изолируем HttpClient от системных прокси Windows (чтобы VPN не портил заголовки)
        var handler = new HttpClientHandler
        {
            UseDefaultCredentials = false,
            UseProxy = false
        };

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.Trim())
        };
    }

    public async Task<string> GenerateArticleAsync(string topic)
    {
        _logger.LogInformation("🤖 [ИИ] Формирую ТЗ для статьи: \"{Topic}\"...", topic);

        string prompt = $@"
        Напиши подробную, экспертную SEO-оптимизированную статью на тему: ""{topic}"".
        Используй реалии текущего 2026 года.
        
        Требования к тексту:
        1. Напиши статью в формате HTML (используй теги <h2>, <h3>, <p>, <ul>, <li>, <strong>). Не используй обертки ```html или теги <html>, <head>, <body>.
        2. Структура: Введение, детальный разбор (3-4 подзаголовка), блок с плюсами и минусами, краткий вывод.
        3. Добавь в текст одну логическую таблицу (тег <table>) с ключевыми характеристиками или сравнением.
        4. Тон автора: дружелюбный, экспертный, без «воды» и банальных фраз. Пиши для людей.
        5. Объем: не менее 4000 знаков.";

        var requestBody = new OpenAiChatRequest
        {
            Model = "gpt-4o-mini",
            Temperature = 0.7f,
            Messages = new[]
            {
                new OpenAiMessage { Role = "system", Content = "Ты профессиональный SEO-копирайтер и эксперт в сфере гаджетов и умного дома." },
                new OpenAiMessage { Role = "user", Content = prompt }
            }
        };

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string jsonString = JsonSerializer.Serialize(requestBody, jsonOptions);

        // СБОРКА НИЗКОУРОВНЕВОГО ЗАПРОСА: Исключаем StringContent
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");

        request.Headers.Clear();
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        request.Headers.TryAddWithoutValidation("Connection", "close");

        // Передаем строго чистые UTF-8 байты, изолируя заголовки от кириллицы в теле
        byte[] jsonBytes = Encoding.UTF8.GetBytes(jsonString);
        var content = new ByteArrayContent(jsonBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Content = content;

        try
        {
            // Отправляем изолированный запрос
            HttpResponseMessage response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<OpenAiChatResponse>(responseString, jsonOptions);
                string? generatedText = result?.Choices?.Length > 0 ? result.Choices[0].Message?.Content : null;

                if (!string.IsNullOrEmpty(generatedText))
                {
                    return generatedText;
                }
            }

            string errorBody = await response.Content.ReadAsStringAsync();
            throw new Exception($"OpenAI API вернул ошибку ({response.StatusCode}): {errorBody}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Исключение при работе с OpenAI API для темы: \"{Topic}\"", topic);
            throw;
        }
    }

    #region Вспомогательные DTO-классы для JSON

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
        [JsonPropertyName("choices")] public Choice[] Choices { get; set; } = Array.Empty<Choice>();
    }

    private class Choice
    {
        [JsonPropertyName("message")] public OpenAiMessage? Message { get; set; }
    }

    #endregion
}
