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
        var sortedParams = parameters
            .Where(p => p.Key != "access_token")
            .OrderBy(p => p.Key)
            .Select(p => $"{p.Key}={p.Value}");

        string paramString = string.Join("", sortedParams);
        string secretPart = GetMd5Hash(accessToken + secretKey);

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

        // 2. Очистка HTML, формирование маскированной CPA-ссылки
        string cleanText = Regex.Replace(textWithFormattedTables.Replace("<p>", "\n\n").Replace("<br>", "\n"), @"<[^>]*>", "");
        string maskedCpaUrl = Application.Helpers.CpaLinkHelper.GenerateMaskedVkLink(title, "tech-info");

        // РАСКОММЕНТИРУЙТЕ СТРОКУ НИЖЕ, КОГДА ТЕСТ ПРОЙДЕТ УСПЕШНО:
        // string finalPostText = $"📰 {title.ToUpper()}\n\n{cleanText}\n\n🚀 Подробности: {maskedCpaUrl}";
        string finalPostText = "Тестовый пост для проверки интеграции API Одноклассников";

        string baseApiUrl = "https://" + "api." + "ok." + "ru/" + "fb.do";
        using var client = new HttpClient();
        string? photoTokenOnly = null; // Храним строго чистую строку токена

        // 3. ЗАГРУЗКА ФОТО В ОК
        if (imageBytes?.Length > 0)
        {
            var uploadRequestParams = new Dictionary<string, string>
            {
                // ИСПОЛЬЗУЕМ ПРАВИЛЬНЫЙ МЕТОД ДЛЯ МЕДИАТОПИКОВ:
                { "method", "photosV2.getAttachmentUploadUrl" },
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

            _logger.LogInformation("[OK] Ответ сервера на getUploadUrl: " + serverResponseStr);
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
            }
        }
        // 4. ПУБЛИКАЦИЯ МЕДИАТОПИКА С ОБЛОЖКОЙ НА СТЕНУ ГРУППЫ

        var jsonOptions = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var mediaList = new List<object>
        {
            new { type = "text", text = finalPostText }
        };

        if (!string.IsNullOrEmpty(photoTokenOnly))
        {
            mediaList.Add(new { type = "photo", id = photoTokenOnly });
        }

        var attachmentRoot = new { media = mediaList };
        string mediaBlocksJson = JsonSerializer.Serialize(attachmentRoot, jsonOptions);

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

        _logger.LogInformation("✅ [OK] Обзор с обложкой успешно опубликован на стену группы!");
        return true;
    }
}
