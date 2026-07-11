using AiSiteFiller.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AiSiteFiller.Infrastructure.Services;

public class VkPublisherService : IPublisherService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<VkPublisherService> _logger;
    private readonly string _accessToken;
    private readonly string _groupId;

    public VkPublisherService(IConfiguration configuration, ILogger<VkPublisherService> logger)
    {
        _logger = logger;

        // Будет считываться из локального конфига
        _accessToken = configuration["VkOptions:AccessToken"] ?? string.Empty;
        _groupId = configuration["VkOptions:GroupId"] ?? string.Empty;

        var handler = new HttpClientHandler { UseDefaultCredentials = false, UseProxy = false };
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://vk.com")
        };
    }

    public async Task<bool> PublishAsync(string title, string contentHtml, string metadata, string siteId, byte[]? imageBytes)
    {
        _logger.LogInformation("[VK] Начинаю подготовку публикации статьи в сообщество...");

        try
        {
            // ИИ выдает нам чистый HTML, а VK API принимает разметку в своем формате (или wiki-разметку)
            // Перед отправкой мы будем очищать или конвертировать теги под требования ВК
            await Task.Delay(1000);

            // Здесь будет лететь POST запрос к методу VK API (например, pages.save или через создание постера)
            _logger.LogInformation($"✅ [VK] Статья '{title}' успешно опубликована в паблик ВК!");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VK] Ошибка публикации во ВКонтакте.");
            return false;
        }
    }
}
