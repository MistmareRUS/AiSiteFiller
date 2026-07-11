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

public class OpenAiGptService : IAiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenAiGptService> _logger;
    private readonly string _apiKey;
    private readonly string _absoluteUrl;

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

        // ЖЕСТКАЯ ВЫЧИСТКА: Удаляем любые не-ASCII символы и скрытый мусор из токена и URL
        _apiKey = Regex.Replace(rawApiKey, @"[^a-zA-Z0-9_\-\+]", "").Trim();
        string cleanBaseUrl = Regex.Replace(rawBaseUrl, @"[^a-zA-Z0-9\.\:\/\-]", "").Trim();

        // Если в конфиге пусто, по умолчанию ставим оригинальный адрес OpenAI
        if (string.IsNullOrEmpty(cleanBaseUrl))
        {
            cleanBaseUrl = "https://api.openai.com";
        }

        // Нормализуем слеши на концах URL
        if (cleanBaseUrl.EndsWith("/"))
        {
            cleanBaseUrl = cleanBaseUrl.TrimEnd('/');
        }

        // СБОРКА АБСОЛЮТНОГО ПУТИ: Теперь адрес будет собираться идеально точно под любой шлюз
        _absoluteUrl = cleanBaseUrl + "/v1/chat/completions";

        var handler = new HttpClientHandler
        {
            UseDefaultCredentials = false,
            UseProxy = false,
            AllowAutoRedirect = false
        };

        _httpClient = new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.Clear();
    }

    public async Task<string> GenerateArticleAsync(string topic)
    {
        _logger.LogInformation("[ИИ] Формирую ТЗ для статьи...");

        string prompt = $@"
            Напиши подробную, экспертную SEO-оптимизированную статью на тему: ""{topic}"".
            Используй реалии текущего { DateTime.Now.Year } года.

            Требования к тексту и оформлению:
            1. Напиши статью в формате HTML (используй теги <h2>, <h3>, <p>, <ul>, <li>, <strong>). Не используй обертки ```html или теги <html>, <head>, <body>.
            2. ВАЖНО: Начинай текст СРАЗУ со вступления (абзаца <p>). НЕ пиши название темы статьи в самом начале и НЕ создавай для него заголовок первого или второго уровня, так как движок сайта уже вывел этот заголовок наверх автоматически.
            3. Структура: Экспертное введение (сразу к сути), детальный разбор нюансов (3-4 подзаголовка <h2> или <h3>), блок с плюсами и минусами, краткий вывод.
            4. Добавь в текст одну логическую таблицу (тег <table>) с ключевыми характеристиками или сравнением.
            5. Тон автора: дружелюбный, экспертный, без «воды» и банальных фраз. Пиши для людей.
            6. Объем: не менее 4000 знаков.";


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

        // Отправляем запрос строго на верифицированный абсолютный URI шлюза
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
            _logger.LogError(ex, "❌ Исключение при работе с OpenAI API.");
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
