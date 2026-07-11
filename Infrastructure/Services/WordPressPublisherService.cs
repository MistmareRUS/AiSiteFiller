using AiSiteFiller.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

    public async Task<bool> PublishAsync(string title, string contentHtml, string category, string siteId, string imageUrl)
    {
        _logger.LogInformation("[WordPress] Поиск конфигурации для сайта: " + siteId);

        var sites = _configuration.GetSection("WordPressSites").Get<List<WordPressSiteConfig>>();
        var currentSite = sites?.Find(s => s.Id == siteId);

        if (currentSite == null)
        {
            _logger.LogError("❌ Конфигурация для сайта Id=" + siteId + " не найдена.");
            return false;
        }

        string cleanUrl = Regex.Replace(currentSite.BaseUrl ?? string.Empty, @"[^a-zA-Z0-9\.\:\/\-]", "").Trim();
        string cleanUsername = Regex.Replace(currentSite.Username ?? string.Empty, @"[^a-zA-Z0-9_\-\.\@]", "").Trim();
        string cleanPassword = Regex.Replace(currentSite.AppPassword ?? string.Empty, @"[^a-zA-Z0-9 ]", "").Trim();

        if (!cleanUrl.StartsWith("http://") && !cleanUrl.StartsWith("https://"))
        {
            cleanUrl = "http://" + cleanUrl;
        }

        var handler = new HttpClientHandler { UseDefaultCredentials = false, UseProxy = false };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri(cleanUrl) };

        string rawCredentials = cleanUsername + ":" + cleanPassword;
        string authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes(rawCredentials));

        // ПЕРВЫЙ ЭТАП: Если ИИ выдал картинку, скачиваем её и загружаем на хостинг Beget
        int? featuredMediaId = null;

        if (!string.IsNullOrEmpty(imageUrl))
        {
            featuredMediaId = await UploadMediaToWordPressAsync(httpClient, authToken, imageUrl, title);
        }

        // ВТОРОЙ ЭТАП: Публикация самой текстовой статьи на сайт
        var wpId = Domain.Constants.AppCategories.GetWordPressId(category);

        var postData = new WordPressPostRequest
        {
            Title = title,
            Content = contentHtml,
            Status = "publish",
            Categories = wpId.HasValue ? new int[] { wpId.Value } : Array.Empty<int>(),
            // Привязываем загруженную картинку как обложку (если загрузка прошла успешно)
            FeaturedMedia = featuredMediaId ?? 0
        };

        string jsonBody = JsonSerializer.Serialize(postData, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var request = new HttpRequestMessage(HttpMethod.Post, "/wp-json/wp/v2/posts");
        request.Headers.TryAddWithoutValidation("Authorization", "Basic " + authToken);
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
            _logger.LogError("❌ Сервер отклонил пост. Код: " + response.StatusCode + ", Ошибка: " + errorContent);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Исключение при отправке статьи на сайт.");
            return false;
        }
    }

    private async Task<int?> UploadMediaToWordPressAsync(HttpClient siteClient, string authToken, string imageUrl, string title)
    {
        _logger.LogInformation("[WordPress] Скачиваю картинку из облака ИИ в память ПК...");

        try
        {
            // Скачиваем бинарный файл картинки из DALL-E/Flux
            using var downloadClient = new HttpClient();
            byte[] imageBytes = await downloadClient.GetByteArrayAsync(imageUrl);

            _logger.LogInformation("[WordPress] Загружаю медиафайл на хостинг Beget...");

            // Формируем пакет multipart/form-data для передачи файла на движок сайта
            var request = new HttpRequestMessage(HttpMethod.Post, "/wp-json/wp/v2/media");
            request.Headers.TryAddWithoutValidation("Authorization", "Basic " + authToken);

            var multipartContent = new MultipartFormDataContent();
            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

            // Генерация безопасного латинского имени файла на основе темы
            string safeFileName = "cover_" + Guid.NewGuid().ToString().Substring(0, 8) + ".jpg";
            multipartContent.Add(imageContent, "file", safeFileName); // Поле обязательно должно называться "file"
            request.Content = multipartContent;

            HttpResponseMessage response = await siteClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                string jsonResponse = await response.Content.ReadAsStringAsync();
                var mediaResult = JsonSerializer.Deserialize<WordPressMediaResponse>(jsonResponse);

                if (mediaResult != null && mediaResult.Id > 0)
                {
                    _logger.LogInformation("✅ Обложка загружена в медиабиблиотеку сайта. ID: " + mediaResult.Id);
                    return mediaResult.Id;
                }
            }

            string errorLog = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("⚠️ Не удалось загрузить картинку на сайт. Ответ сервера: " + errorLog);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Сбой при обработке медиафайла обложки: " + ex.Message);
            return null;
        }
    }

    #region Вспомогательные DTO-классы

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

        [JsonPropertyName("featured_media")] public int FeaturedMedia { get; set; } // ID привязанной картинки
    }

    private class WordPressMediaResponse
    {
        [JsonPropertyName("id")] public int Id { get; set; }
    }

    #endregion
}
