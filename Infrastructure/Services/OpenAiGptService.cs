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
    private readonly string _absoluteUrl;
    private readonly IConfiguration _configuration;

    public OpenAiGptService(IConfiguration configuration, ILogger<OpenAiGptService> logger)
    {
        _logger = logger;
        _configuration = configuration;

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

        var requestBody = new OpenAiChatRequest
        {
            Model = "gpt-4o-mini",
            Temperature = 0.7f,
            Messages = new[]
            {
                new OpenAiMessage { Role = "system", Content = "Ты — профессиональный технический копирайтер, эксперт по гаджетам и умному дому. " +
                    "Твоя задача — написать подробный, честный и глубокий аналитический обзор на тему, которую укажет пользователь. " +
                    "Ты должен генерировать статью строго в формате валидного HTML (используй теги <h2>, <h3>, <p>, <ul>, <li>). " +
                    "Внутри статьи ОБЯЗАТЕЛЬНО должна быть одна подробная сравнительная HTML-таблица (<table>) с техническими характеристиками моделей. " +
                    "КРИТИЧЕСКОЕ ТРЕБОВАНИЕ: Ты обязан органично и естественно вплести в текст экспертные LSI-фразы, " +
                    "соответствующие тематике (например: 'автономность', 'производительность в играх', 'мощность всасывания в Паскалях', " +
                    "'построение карты помещения через LiDAR', 'уровень шума', 'интеграция с умным домом', 'соотношение цена-качество'). " +
                    "Текст не должен выглядеть как спам, фразы должны идеально подходить по смыслу. Не используй теги <html>, <head> или <body>." +
                    "Также в конце обзора, а также внутри сравнительной таблицы в строке 'Где купить', " +
                    "ты ОБЯЗАТЕЛЬНО должен вставить стандартизированную текстовую заглушку строго в формате: " +
                    "[CPA_LINK_PLACEHOLDER]. Ничего другого вместо неё не пиши, наш парсер заменит её автоматически."
                },
                new OpenAiMessage { Role = "user", Content = "Напиши подробную SEO-оптимизированную статью на тему: \"" + topic + "\". " +
                    "Начни сразу с текста статьи, не пиши приветствий и вступлений вроде 'Вот ваш обзор'. " +
                    "Обязательно включи в текст сравнительную таблицу характеристик <table> и используй LSI-термины." 
                }
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

    public async Task<string> GenerateImageAsync(string topic)
    {
        _logger.LogInformation("[ИИ] Перевожу тему статьи в англоязычный промпт для GPU...");

        // 1. Формируем микро-запрос к текстовому ИИ для создания правильного промпта
        string translationPrompt = "Напиши краткое (до 15 слов) описание для генерации картинки в Stable Diffusion на английском языке для статьи на тему: \"" + topic + "\". " +
                                   "В описании должен быть только конкретный предмет статьи крупным планом. Не используй абстрактные понятия. Ответь СТРОГО на английском языке, без кавычек и вводных слов.";

        var requestBodyText = new
        {
            model = "gpt-4o-mini",
            temperature = 0.5f,
            messages = new[]
            {
            new { role = "system", content = "You are a professional prompt engineer for Stable Diffusion. Output only pure English prompt." },
            new { role = "user", content = translationPrompt }
        }
        };

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string textJsonString = JsonSerializer.Serialize(requestBodyText, jsonOptions);

        // Сначала стучимся в текстовый ИИ шлюза ProxyAPI
        var textRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_absoluteUrl));
        textRequest.Headers.Clear();
        textRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
        textRequest.Headers.TryAddWithoutValidation("Connection", "close");

        var textContent = new ByteArrayContent(Encoding.UTF8.GetBytes(textJsonString));
        textContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        textRequest.Content = textContent;

        string englishVisualPrompt = "Modern high-tech gadget placeholder";
        try
        {
            HttpResponseMessage textResponse = await _httpClient.SendAsync(textRequest);
            if (textResponse.IsSuccessStatusCode)
            {
                string textResponseString = await textResponse.Content.ReadAsStringAsync();
                using var textDoc = JsonDocument.Parse(textResponseString);
                var choices = textDoc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0)
                {
                    englishVisualPrompt = choices[0].GetProperty("message").GetProperty("content").GetString() ?? englishVisualPrompt;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Не удалось перевести промпт через ИИ, использую базовый: " + ex.Message);
        }

        // Вычищаем промпт от возможных кавычек, которые ИИ мог случайно вернуть
        englishVisualPrompt = englishVisualPrompt.Trim('"', '\'', ' ');
        _logger.LogInformation("[Локальный ИИ] Итоговый англоязычный промпт для 4070 Super: " + englishVisualPrompt);

        // 2. Формируем промпт: добавляем чистоту, студийный свет и явный запрет текста
        string localPrompt = "High-end commercial product photography of " + englishVisualPrompt +
            ", premium studio lighting, hyperrealistic matte textures, sharp focus, cinematic composition, no branding, no text.";

        // Оптимизируем настройки для Juggernaut Ragnarok (фотореализм без "каракулей")
        var imageRequestBody = new
        {
            prompt = localPrompt,
            // Усиленный негатив для удаления букв, текста и логотипов
            negative_prompt = "(text:1.4), (words:1.4), (letters:1.4), (font:1.4), (typography:1.4), logo, watermark, signature, blurred, illustration, cartoon",
            steps = 30,
            cfg_scale = 4.5,        // Низкий CFG = меньше "галлюцинаций" и надписей
            width = 1024,
            height = 576,
            sampler_name = "DPM++ 2M Karras" // Топовый сэмплер для текстур
        };


        string imageJsonString = JsonSerializer.Serialize(imageRequestBody, jsonOptions);
        string localSdUrl = "http://localhost:7860/sdapi/v1/txt2img";

        var socketsHandler = new SocketsHttpHandler { UseProxy = false, Proxy = null, AllowAutoRedirect = false };
        using var localClient = new HttpClient(socketsHandler);
        localClient.DefaultRequestHeaders.Clear();
        localClient.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(localSdUrl));
        byte[] jsonBytes = Encoding.UTF8.GetBytes(imageJsonString);
        var content = new ByteArrayContent(jsonBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Content = content;

        try
        {
            _logger.LogInformation("[Локальный ИИ] Отправляю прямой запрос на GPU по адресу: " + localSdUrl);
            HttpResponseMessage response = await localClient.SendAsync(request);
            string responseString = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(responseString);
                var imagesArray = doc.RootElement.GetProperty("images");
                if (imagesArray.ValueKind == JsonValueKind.Array && imagesArray.GetArrayLength() > 0)
                {
                    _logger.LogInformation("✅ [Локальный ИИ] Картинка успешно сгенерирована на GPU!");
                    return imagesArray[0].GetString() ?? string.Empty;
                }
            }
            throw new Exception("Локальный SD API вернул ошибку: " + response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError("❌ Ошибка локальной генерации на GPU: " + ex.Message);
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

    #region Вспомогательные DTO-классы для картинок
    private class OpenAiImageRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "gpt-image-1.5";

        [JsonPropertyName("prompt")] public string Prompt { get; set; } = string.Empty;
        [JsonPropertyName("n")] public int N { get; set; } = 1;
        [JsonPropertyName("size")] public string Size { get; set; } = "1024x1024";
    }

    private class OpenAiImageResponse
    {
        [JsonPropertyName("data")] public ImageData[]? Data { get; set; }
    }

    private class ImageData
    {
        // Вместо [JsonPropertyName("url")]
        [JsonPropertyName("b64_json")]
        public string B64Json { get; set; } = string.Empty;
    }
    #endregion

}
