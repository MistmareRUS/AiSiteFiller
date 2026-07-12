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
    
    public string PlatformName => "WordPress";

    public WordPressPublisherService(IConfiguration configuration, ILogger<WordPressPublisherService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> PublishAsync(string title, string contentHtml, string category, string siteId, byte[]? imageBytes)
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

        int? featuredMediaId = null;

        // Если байты картинки переданы, выгружаем их на хостинг Beget
        if (imageBytes != null && imageBytes.Length > 0)
        {
            featuredMediaId = await UploadMediaBytesToWordPressAsync(httpClient, authToken, imageBytes, title);
        }

        var wpId = Domain.Constants.AppCategories.GetWordPressId(category);

        var postData = new WordPressPostRequest
        {
            Title = title,
            Content = contentHtml,
            Status = "publish",
            Categories = wpId.HasValue ? new int[] { wpId.Value } : Array.Empty<int>(),
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
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Исключение при отправке статьи на сайт.");
            return false;
        }
    }

    private async Task<int?> UploadMediaBytesToWordPressAsync(HttpClient siteClient, string authToken, byte[] imageBytes, string title)
    {
        _logger.LogInformation("[WordPress] Загружаю готовые байты обложки на хостинг Beget...");

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/wp-json/wp/v2/media");
            request.Headers.TryAddWithoutValidation("Authorization", "Basic " + authToken);

            var multipartContent = new MultipartFormDataContent();
            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

            string safeFileName = "cover_" + Guid.NewGuid().ToString().Substring(0, 8) + ".jpg";
            multipartContent.Add(imageContent, "file", safeFileName);
            request.Content = multipartContent;

            HttpResponseMessage response = await siteClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                string jsonResponse = await response.Content.ReadAsStringAsync();
                var mediaResult = JsonSerializer.Deserialize<WordPressMediaResponse>(jsonResponse);
                if (mediaResult != null && mediaResult.Id > 0)
                {
                    _logger.LogInformation("✅ Обложка успешно синхронизирована с медиабиблиотекой сайта. ID: " + mediaResult.Id);
                    return mediaResult.Id;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("⚠️ Сбой при выгрузке медиабайтов: " + ex.Message);
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
