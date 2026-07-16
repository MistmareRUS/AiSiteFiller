using AiSiteFiller.Application.Interfaces;
using AiSiteFiller.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AiSiteFiller.Infrastructure.Services;

public class VcPublisherService : IPublisherService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiToken;
    private readonly string _subsiteId;
    private readonly ILogger<VcPublisherService> _logger;

    public string PlatformName => "VC";
    public PublicationType PublishType => PublicationType.FullSeoArticle;


    public VcPublisherService(HttpClient httpClient, IConfiguration configuration, ILogger<VcPublisherService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;

        // Вычитываем токен авторизации и ID подсайта из конфигурации по аналогии с Teletype
        _apiToken = (configuration["VcOptions:ApiToken"] ?? "").Trim();
        _subsiteId = (configuration["VcOptions:SubsiteId"] ?? "0").Trim();
    }

    public async Task<bool> PublishAsync(string title, string contentHtml, string metadata, string siteId, byte[]? imageBytes)
    {
        if (string.IsNullOrEmpty(_apiToken))
        {
            _logger.LogError("[VC] Ошибка: В конфигурации отсутствует ApiToken для VC.ru.");
            return false;
        }

        try
        {
            _logger.LogInformation("[VC] Начинаю прямую публикацию статьи по API Osnova...");

            // Жесткое атомарное правило сборки URL для защиты от багов парсинга UI
            string baseApiUrl = "https://" + "api." + "vc." + "ru/" + "v2.1/" + "entry";

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Авторизация в API Osnova выполняется через кастомный заголовок
            _httpClient.DefaultRequestHeaders.Add("X-Device-Token", _apiToken);

            int subsiteIdParsed = int.TryParse(_subsiteId, out int id) ? id : 0;

            // Формируем полезную нагрузку. API VC требует разбиения на блоки.
            // Передаем весь HTML контент в стандартный текстовый блок (он поддерживает базовые теги)
            var payload = new
            {
                title = title,
                subsite_id = subsiteIdParsed,
                blocks = new[]
                {
                    new
                    {
                        type = "text",
                        data = new
                        {
                            text = contentHtml,
                            format = "html"
                        }
                    }
                }
            };

            string jsonPayload = JsonSerializer.Serialize(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.PostAsync(baseApiUrl, content);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation($"✅ [VC] Статья успешно опубликована на платформе VC.ru! Тема: {title}");
                return true;
            }

            string errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError($"[VC] ❌ Сбой публикации. Код ответа: {response.StatusCode}. Ответ сервера: {errorContent}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError("[VC] КРИТИЧЕСКАЯ ОШИБКА отправки запроса: " + ex.Message);
            return false;
        }
    }
}
