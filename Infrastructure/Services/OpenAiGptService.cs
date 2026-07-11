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

        if (string.IsNullOrEmpty(baseUrl))
        {
            baseUrl = "https://openai.com";
        }

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl)
        };

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<string> GenerateArticleAsync(string topic)
    {
        _logger.LogInformation("🤖 Отправляю запрос к ИИ на генерацию статьи по теме: \"{Topic}\"...", topic);

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
        var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await _httpClient.PostAsync("/v1/chat/completions", content);

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<OpenAiChatResponse>(responseString, jsonOptions);
                string? generatedText = result?.Choices?[0].Message?.Content;

                if (!string.IsNullOrEmpty(generatedText))
                {
                    _logger.LogInformation("✅ ИИ успешно сгенерировал текст статьи для темы: \"{Topic}\"", topic);
                    return generatedText;
                }
            }

            string errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("❌ Ошибка ответа OpenAI API. Статус: {Status}, Тело: {Error}", response.StatusCode, errorBody);
            throw new Exception($"OpenAI API вернул ошибку ({response.StatusCode})");
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
        [JsonPropertyName("choices")] public Choice[]? Choices { get; set; }
    }

    private class Choice
    {
        [JsonPropertyName("message")] public OpenAiMessage? Message { get; set; }
    }

    #endregion
}
