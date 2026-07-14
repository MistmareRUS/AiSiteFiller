using AiSiteFiller.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiSiteFiller.Infrastructure.Services;

public class OkPublisherService : IPublisherService
{
    private readonly string _accessToken, _groupId, _applicationKey, _secretKey;
    private readonly ILogger<OkPublisherService> _logger;
    public string PlatformName => "OK";

    // Инициализация, проверка настроек и парсер таблиц из VkPublisherService
    public OkPublisherService(IConfiguration configuration, ILogger<OkPublisherService> logger)
    {
        _logger = logger;
        _accessToken = (configuration["OkOptions:AccessToken"] ?? "").Replace("\n", "").Trim();
        _groupId = (configuration["OkOptions:GroupId"] ?? "").Replace("\n", "").Trim();
        _applicationKey = (configuration["OkOptions:ApplicationKey"] ?? "").Replace("\n", "").Trim();
        _secretKey = (configuration["OkOptions:SecretKey"] ?? "").Replace("\n", "").Trim();
    }

    private string CalculateSignature(Dictionary<string, string> parameters, string accessToken, string secretKey)
    {
        // 1. Сортируем параметры по алфавиту ключей
        var sortedParams = parameters
            .Where(p => p.Key != "access_token") // Токен не участвует в первой части подписи
            .OrderBy(p => p.Key)
            .Select(p => $"{p.Key}={p.Value}");

        string paramString = string.Join("", sortedParams);

        // 2. Добавляем MD5 от связки (access_token + secret_key)
        string secretPart = GetMd5Hash(accessToken + secretKey);

        // 3. Финальный MD5 от склеенной строки параметров и секретной части
        return GetMd5Hash(paramString + secretPart);
    }

    private string GetMd5Hash(string input)
    {
        using MD5 md5 = MD5.Create();
        byte[] inputBytes = Encoding.UTF8.GetBytes(input);
        byte[] hashBytes = md5.ComputeHash(inputBytes);
        return Convert.ToHexString(hashBytes).ToLower(); // ОК принимает sig в нижнем регистре
    }

    public async Task<bool> PublishAsync(string title, string contentHtml, string metadata, string siteId, byte[]? imageBytes)
    {
        if (string.IsNullOrEmpty(_accessToken) || string.IsNullOrEmpty(_groupId)) return false;

        string textWithFormattedTables = contentHtml;
        try
        {
            // Используем Regex для поиска таблиц, строк и ячеек
            var tableMatches = Regex.Matches(textWithFormattedTables, @"<table[^>]*>(.*?)<\/table>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match tableMatch in tableMatches)
            {
                var rows = Regex.Matches(tableMatch.Groups[1].Value, @"<tr[^>]*>(.*?)<\/tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (rows.Count <= 1) continue;

                var sbTable = new StringBuilder();
                sbTable.AppendLine("\n 📊 СРАВНИТЕЛЬНЫЕ ХАРАКТЕРИСТИКИ МОДЕЛЕЙ:");
                sbTable.AppendLine("───────────────────────────────────");

                // Обработка заголовков
                var headers = Regex.Matches(rows[0].Value, @"<th[^>]*>(.*?)<\/th>|<td[^>]*>(.*?)<\/td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                                   .Cast<Match>()
                                   .Select(m => Regex.Replace(m.Value, @"<[^>]*>", "").Trim()).ToList();

                // Обработка строк данных
                for (int i = 1; i < rows.Count; i++)
                {
                    var cells = Regex.Matches(rows[i].Value, @"<td[^>]*>(.*?)<\/td>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (cells.Count == 0) continue;

                    sbTable.AppendLine("🔹 " + Regex.Replace(cells[0].Value, @"<[^>]*>", "").Trim().ToUpper());
                    for (int j = 1; j < cells.Count && j < headers.Count; j++)
                        sbTable.AppendLine(" ▪ " + headers[j] + ": " + Regex.Replace(cells[j].Value, @"<[^>]*>", "").Trim());
                    sbTable.AppendLine();
                }
                sbTable.AppendLine("───────────────────────────────────");
                textWithFormattedTables = textWithFormattedTables.Replace(tableMatch.Value, sbTable.ToString());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[OK] Ошибка конвертации таблицы: " + ex.Message); //
        }

        // 2. Очистка HTML, формирование маскированной CPA-ссылки
        string cleanText = Regex.Replace(textWithFormattedTables.Replace("<p>", "\n\n").Replace("<br>", "\n"), @"<[^>]*>", "");
        string maskedCpaUrl = Application.Helpers.CpaLinkHelper.GenerateMaskedVkLink(title, "tech-info");

        string finalPostText = $"📰 {title.ToUpper()}\n\n{cleanText}\n\n🚀 Подробности: {maskedCpaUrl}";

        // 3. ЗАГРУЗКА ФОТО В ОК ЧЕРЕЗ КЛАССИЧЕСКИЙ REST API ГРУППЫ
        string photoAttachmentJson = "";
        if (imageBytes?.Length > 0)
        {
            using var client = new HttpClient();

            // 1. Собираем базовые параметры (без sig и access_token)
            var uploadRequestParams = new Dictionary<string, string>
            {
                { "method", "photosV2.getUploadUrl" },
                { "application_key", _applicationKey },
                { "gid", _groupId }, // <--- Указываем ID группы числом (например, "642398471239")
                { "count", "1" }
            };


            // 2. Считаем подпись
            string sig = CalculateSignature(uploadRequestParams, _accessToken, _secretKey);

            // 3. Формируем итоговый URL со всеми параметрами
            // Ссылка разбита по правилу: " + "
            string baseApiUrl = "https://apiok.ru" + "/fb.do";

            var finalParams = new List<string>();
            foreach (var kp in uploadRequestParams)
            {
                finalParams.Add($"{kp.Key}={Uri.EscapeDataString(kp.Value)}");
            }
            finalParams.Add($"access_token={Uri.EscapeDataString(_accessToken)}");
            finalParams.Add($"sig={sig}");

            string requestUrl = $"{baseApiUrl}?{string.Join("&", finalParams)}";

            // 4. Отправляем запрос через HttpClient
            using var httpClient = new HttpClient();
            var serverResponse = await httpClient.PostAsync(requestUrl, null); // Для GET/POST без тела
            string serverResponseStr = await serverResponse.Content.ReadAsStringAsync();


            _logger.LogInformation("[OK] Ответ сервера на getUploadUrl: " + serverResponseStr);
            using var serverData = JsonDocument.Parse(serverResponseStr);

            // Если Одноклассники успешно выдали внутренний upload_url
            if (serverData.RootElement.TryGetProperty("upload_url", out var urlEl))
            {
                // Шаг Б: Загружаем массив байт multipart-запросом
                using var content = new MultipartFormDataContent { { new ByteArrayContent(imageBytes), "file", "img.jpg" } };
                var uploadResponse = await client.PostAsync(urlEl.GetString(), content);
                string uploadResponseStr = await uploadResponse.Content.ReadAsStringAsync();
                using var uploadData = JsonDocument.Parse(uploadResponseStr);

                // Вытаскиваем ID и токен загруженного фото
                if (uploadData.RootElement.TryGetProperty("photos", out var photosEl) && photosEl.GetArrayLength() > 0)
                {
                    var firstPhoto = photosEl[0];
                    if (firstPhoto.TryGetProperty("token", out var tokenEl))
                    {
                        // Формируем блок вложения по официальной спецификации OK REST API
                        photoAttachmentJson = "{\"type\":\"photo\",\"id\":\"" + tokenEl.GetString() + "\"}";
                    }
                }
            }
        }

        // 4. ПУБЛИКАЦИЯ МЕДИАТОПИКА С ОБЛОЖКОЙ НА СТЕНУ ГРУППЫ
        using var finalClient = new HttpClient();
        string restRequestUrl = "https://ok.ru";

        // Собираем медиа-блоки темы: текст статьи + блок фотографии (если она успешно загрузилась)
        string mediaBlocksJson = "[{\"type\":\"text\",\"text\":\"" + System.Web.HttpUtility.JavaScriptStringEncode(finalPostText) + "\"}";
        if (!string.IsNullOrEmpty(photoAttachmentJson))
        {
            mediaBlocksJson += "," + photoAttachmentJson;
        }
        mediaBlocksJson += "]";

        // Упаковываем параметры для mediatopic.post
        var requestParams = new System.Collections.Generic.Dictionary<string, string>
        {
            { "method", "mediatopic.post" },
            { "gid", _groupId },
            { "type", "GROUP_THEME" }, // Публикация в ленту сообщества
            { "attachment", mediaBlocksJson },
            { "access_token", _accessToken }
        };

        var formContent = new FormUrlEncodedContent(requestParams);
        var response = await finalClient.PostAsync(restRequestUrl, formContent);
        string resultStr = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode || resultStr.Contains("error_code"))
        {
            throw new Exception("Ошибка OK REST API: " + resultStr);
        }

        _logger.LogInformation("✅ [OK] Обзор с обложкой успешно опубликован на стену группы!");
        return true;

    }
}
