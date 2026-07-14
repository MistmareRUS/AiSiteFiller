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
            .Where(p => p.Key != "access_token")
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
        return Convert.ToHexString(hashBytes).ToLower();
    }

    public async Task<bool> PublishAsync(string title, string contentHtml, string metadata, string siteId, byte[]? imageBytes)
    {
        if (string.IsNullOrEmpty(_accessToken) || string.IsNullOrEmpty(_groupId)) return false;

        // ==========================================
        // ШАГ 1: ФОРМАТИРОВАНИЕ ТАБЛИЦ ХАРАКТЕРИСТИК
        // ==========================================
        string textWithFormattedTables = contentHtml;
        try
        {
            var tableMatches = Regex.Matches(textWithFormattedTables, @"<table[^>]*>(.*?)<\/table>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match tableMatch in tableMatches)
            {
                var rows = Regex.Matches(tableMatch.Groups[1].Value, @"<tr[^>]*>(.*?)<\/tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (rows.Count <= 1) continue;

                var sbTable = new StringBuilder();
                sbTable.AppendLine("\n 📊 СРАВНИТЕЛЬНЫЕ ХАРАКТЕРИСТИКИ МОДЕЛЕЙ:");
                sbTable.AppendLine("───────────────────────────────────");

                var headers = Regex.Matches(rows[0].Value, @"<th[^>]*>(.*?)<\/th>|<td[^>]*>(.*?)<\/td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                                   .Cast<Match>()
                                   .Select(m => Regex.Replace(m.Value, @"<[^>]*>", "").Trim()).ToList();

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
            _logger.LogWarning("[OK] Ошибка конвертации таблицы: " + ex.Message);
        }
        // ==========================================
        // ШАГ 2: ОЧИСТКА HTML И ПОДГОТОВКА ССЫЛОК
        // ==========================================

        // 1. Вытягиваем партнерский маскированный URL для рекламного сниппета внизу поста
        string maskedCpaUrl = Application.Helpers.CpaLinkHelper.GenerateMaskedVkLink(title, "tech-info");

        // 2. Очищаем HTML-теги, превращая абзацы и переносы строк в текстовый формат
        string cleanText = Regex.Replace(textWithFormattedTables.Replace("<p>", "\n\n").Replace("<br>", "\n"), @"<[^>]*>", "");

        // ВАЖНО: Мы БОЛЬШЕ НЕ ВЫРЕЗАЕМ ссылки через Regex и НЕ ОБРЕЗАЕМ текст через Substring!
        // Формируем полноценную, длинную статью со всеми вашими CPA-ссылками Яндекс Маркета и таблицами
        string finalPostText = $"📰 {title.ToUpper()}\n\n{cleanText}\n\n🚀 Подробности и цены: {maskedCpaUrl}";

        string baseApiUrl = "https://" + "api." + "ok." + "ru/" + "fb.do";
        using var client = new HttpClient();
        string? photoTokenOnly = null;

        // ==========================================
        // ШАГ 3: ЗАГРУЗКА ФОТОГРАФИИ НА СЕРВЕР ОК
        // ==========================================
        if (imageBytes?.Length > 0)
        {
            var uploadRequestParams = new Dictionary<string, string>
            {
                { "method", "photosV2.getUploadUrl" },
                { "application_key", _applicationKey },
                { "gid", _groupId },
                { "count", "1" }
            };

            string sigUpload = CalculateSignature(uploadRequestParams, _accessToken, _secretKey);

            var uploadUrlParams = new List<string>();
            foreach (var kp in uploadRequestParams)
            {
                uploadUrlParams.Add($"{kp.Key}={Uri.EscapeDataString(kp.Value)}");
            }
            uploadUrlParams.Add($"access_token={Uri.EscapeDataString(_accessToken)}");
            uploadUrlParams.Add($"sig={sigUpload}");

            string requestUploadUrl = $"{baseApiUrl}?{string.Join("&", uploadUrlParams)}";

            var serverResponse = await client.PostAsync(requestUploadUrl, null);
            string serverResponseStr = await serverResponse.Content.ReadAsStringAsync();

            using var serverData = JsonDocument.Parse(serverResponseStr);

            if (serverData.RootElement.TryGetProperty("upload_url", out var urlEl))
            {
                using var content = new MultipartFormDataContent { { new ByteArrayContent(imageBytes), "file", "img.jpg" } };
                var uploadResponse = await client.PostAsync(urlEl.GetString(), content);
                string uploadResponseStr = await uploadResponse.Content.ReadAsStringAsync();

                _logger.LogInformation("[OK] Ответ сервера после загрузки файла: " + uploadResponseStr);
                using var uploadData = JsonDocument.Parse(uploadResponseStr);

                if (uploadData.RootElement.TryGetProperty("photos", out var photosEl) && photosEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var photoProperty in photosEl.EnumerateObject())
                    {
                        if (photoProperty.Value.TryGetProperty("token", out var tokenEl))
                        {
                            photoTokenOnly = tokenEl.GetString();
                            break;
                        }
                    }
                }
                else if (uploadData.RootElement.TryGetProperty("token", out var rootTokenEl))
                {
                    photoTokenOnly = rootTokenEl.GetString();
                }

                // Очищаем токен от Unicode-кодирования .NET, возвращая знаки '+' и '/'
                if (!string.IsNullOrEmpty(photoTokenOnly))
                {
                    photoTokenOnly = photoTokenOnly
                        .Replace("\\u002B", "+")
                        .Replace("\\u002b", "+")
                        .Replace("\\/", "/");
                    _logger.LogInformation("✅ [OK] Токен фотографии успешно получен: " + photoTokenOnly);
                }
            }
        }

        // ==========================================
        // ШАГ 4: ФОРМИРОВАНИЕ ATTACHMENT И ПУБЛИКАЦИЯ
        // ==========================================

        // Возвращаем ваш оригинальный текст (убираем обрезку Substring!)
        string safeTextForJson = finalPostText
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n")
            .Replace("\r", "\\n");

        string safeCpaUrl = maskedCpaUrl.Replace("\\", "\\\\").Replace("\"", "\\\"");

        var sbJson = new StringBuilder();
        sbJson.Append("{\"media\":[");

        // 1. Блок текста
        sbJson.Append("{\"type\":\"text\",\"text\":\"").Append(safeTextForJson).Append("\"}");

        // 2. Блок фото. КРИТИЧЕСКИЙ ФИКС ДЛЯ ОК API ГРУПП:
        // Если используется токен, Одноклассники для GROUP_THEME требуют, 
        // чтобы он передавался в поле "id", но перед ним ОБЯЗАТЕЛЬНО должна идти обертка photos
        if (!string.IsNullOrEmpty(photoTokenOnly))
        {
            // Каноничный синтаксис ОК для временных токенов в групповых темах
            sbJson.Append(",{\"type\":\"photo\",\"list\":[{\"id\":\"").Append(photoTokenOnly).Append("\"}]}");
        }

        // 3. Блок ссылки
        sbJson.Append(",{\"type\":\"link\",\"url\":\"").Append(safeCpaUrl).Append("\"}");
        sbJson.Append("]}");

        string mediaBlocksJson = sbJson.ToString();

        var postRequestParams = new Dictionary<string, string>
        {
            { "method", "mediatopic.post" },
            { "application_key", _applicationKey },
            { "gid", _groupId },
            { "type", "GROUP_THEME" },
            { "attachment", mediaBlocksJson }
        };

        string sigPost = CalculateSignature(postRequestParams, _accessToken, _secretKey);
        postRequestParams.Add("access_token", _accessToken);
        postRequestParams.Add("sig", sigPost);

        var paramList = new List<string>();
        foreach (var kvp in postRequestParams)
        {
            paramList.Add($"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}");
        }
        string requestBody = string.Join("&", paramList);

        using var stringContent = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        var response = await client.PostAsync(baseApiUrl, stringContent);
        string resultStr = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode || resultStr.Contains("error_code"))
        {
            throw new Exception("Ошибка OK REST API: " + resultStr);
        }

        _logger.LogInformation("✅ [OK] Полноценный обзор с обложкой и CPA-сниппетом успешно опубликован на стену группы!");
        return true;
    }
}
