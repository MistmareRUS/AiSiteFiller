using AiSiteFiller.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace AiSiteFiller.Infrastructure.Services;

public class VkPublisherService : IPublisherService
{
    private readonly string _accessToken;
    private readonly string _groupId;
    private readonly ILogger<VkPublisherService> _logger;

    public VkPublisherService(IConfiguration configuration, ILogger<VkPublisherService> logger)
    {
        _logger = logger;
        _accessToken = (configuration["VkOptions:AccessToken"] ?? string.Empty).Replace("\n", "").Replace("\r", "").Trim();
        _groupId = (configuration["VkOptions:GroupId"] ?? string.Empty).Replace("\n", "").Replace("\r", "").Trim();
    }

    public async Task<bool> PublishAsync(string title, string contentHtml, string metadata, string siteId, byte[]? imageBytes)
    {
        string cleanToken = _accessToken;
        string cleanGroup = _groupId;

        if (string.IsNullOrEmpty(cleanToken) || string.IsNullOrEmpty(cleanGroup))
        {
            _logger.LogWarning("[VK] Настройки VK не заданы в appsettings.local.json. Пропускаю.");
            return false;
        }

        _logger.LogInformation("[VK] Начинаю процесс публикации иллюстрированного поста на стену...");

        try
        {
            // 1. Форматируем HTML текст в красивый пост для соцсетей
            string cleanText = contentHtml
                .Replace("<p>", "").Replace("</p>", "\n\n")
                .Replace("<h2>", "🔥 ").Replace("</h2>", " \n")
                .Replace("<h3>", "⚡ ").Replace("</h3>", " \n")
                .Replace("<ul>", "").Replace("</ul>", "")
                .Replace("<li>", "🔹 ").Replace("</li>", "\n")
                .Replace("strong", "").Replace("</strong>", "");

            cleanText = System.Text.RegularExpressions.Regex.Replace(cleanText, @"<[^>]*>", "");

            string finalPostText = "📰 " + title.ToUpper() + "\n\n" + cleanText;

            string targetGroupId = cleanGroup.Replace("-", "").Trim();
            string attachmentsId = string.Empty;

            // Создаем изолированный клиент для работы с API
            var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var isolatedClient = new HttpClient(handler);
            isolatedClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            // ПОДБЛОК ЗАГРУЗКИ КАРТИНКИ В ВК: Выполняем только если байты обложки успешно извлечены из MongoDB GridFS
            // ПОДБЛОК ЗАГРУЗКИ КАРТИНКИ В ВК
            if (imageBytes != null && imageBytes.Length > 0)
            {
                _logger.LogInformation("[VK] Обложка обнаружена. Запускаю трехшаговую загрузку медиа-файла на сервера ВКонтакте...");

                // ШАГ А: Получаем адрес сервера
                string uploadServerUri = "https://" + "api." + "vk.com" + "/method/" + "photos.getWallUploadServer" + "?v=5.131&group_id=" + targetGroupId + "&access_token=" + cleanToken;
                var serverResponse = await isolatedClient.PostAsync(uploadServerUri, null);
                string serverResultStr = Encoding.UTF8.GetString(await serverResponse.Content.ReadAsByteArrayAsync()).Trim();

                _logger.LogInformation("[VK Дебаг ШАГ А]: " + serverResultStr); // Выводим ответ шага А

                using var serverDoc = JsonDocument.Parse(serverResultStr);
                if (serverDoc.RootElement.TryGetProperty("response", out var uploadResponseEl))
                {
                    string uploadUrl = uploadResponseEl.GetProperty("upload_url").GetString() ?? string.Empty;

                    if (!string.IsNullOrEmpty(uploadUrl))
                    {
                        // ШАГ Б: Отправляем байты картинки POST-запросом
                        using var multipartContent = new MultipartFormDataContent();
                        var imageContent = new ByteArrayContent(imageBytes);
                        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                        multipartContent.Add(imageContent, "photo", "cover.jpg");

                        var uploadResponse = await isolatedClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = multipartContent });
                        string uploadResultStr = Encoding.UTF8.GetString(await uploadResponse.Content.ReadAsByteArrayAsync()).Trim();

                        _logger.LogInformation("[VK Дебаг ШАГ Б]: " + uploadResultStr); // Выводим ответ шага Б

                        using var uploadDoc = JsonDocument.Parse(uploadResultStr);
                        if (uploadDoc.RootElement.TryGetProperty("server", out var serverEl) &&
                            uploadDoc.RootElement.TryGetProperty("photo", out var photoEl) &&
                            uploadDoc.RootElement.TryGetProperty("hash", out var hashEl))
                        {
                            // ШАГ В: Сохраняем фотографию в медиатеку ВК
                            string savePhotoUri = "https://" + "api." + "vk.com" + "/method/" + "photos.saveWallPhoto" +
                                                  "?v=5.131" +
                                                  "&group_id=" + targetGroupId +
                                                  "&server=" + serverEl.GetInt32() +
                                                  "&photo=" + Uri.EscapeDataString(photoEl.GetString() ?? "") +
                                                  "&hash=" + hashEl.GetString() +
                                                  "&access_token=" + cleanToken;

                            var saveResponse = await isolatedClient.PostAsync(savePhotoUri, null);
                            string saveResultStr = Encoding.UTF8.GetString(await saveResponse.Content.ReadAsByteArrayAsync()).Trim();

                            using var saveDoc = JsonDocument.Parse(saveResultStr);

                            // Если на финальном шаге ВК вернул ошибку — пробрасываем её наружу для MessageBox
                            if (saveDoc.RootElement.TryGetProperty("error", out var saveErrorEl))
                            {
                                string msg = saveErrorEl.GetProperty("error_msg").GetString() ?? "Unknown";
                                int code = saveErrorEl.GetProperty("error_code").GetInt32();
                                throw new Exception("Ошибка photos.saveWallPhoto [Код " + code + "]: " + msg + "\nОтвет сервера: " + saveResultStr);
                            }

                            if (saveDoc.RootElement.TryGetProperty("response", out var saveResponseArray) && saveResponseArray.GetArrayLength() > 0)
                            {
                                // ЖЕЛЕЗНЫЙ ФИКС: Достаем первый элемент массива через индексатор JsonElement!
                                var savedPhotoEl = saveResponseArray[0];

                                long ownerId = savedPhotoEl.GetProperty("owner_id").GetInt64();
                                long photoId = savedPhotoEl.GetProperty("id").GetInt64();

                                // Формируем итоговый attachment-строковый ID для wall.post
                                attachmentsId = "photo" + ownerId + "_" + photoId;
                                _logger.LogInformation("✅ [VK] Обложка успешно импортирована! Медиа-ID: " + attachmentsId);
                            }
                        }
                    }
                }
            }

            // 2. ФИНАЛЬНАЯ ПУБЛИКАЦИЯ НА СТЕНУ: Вызываем наш рабочий и проверенный wall.post через конкатенацию
            string requestUri = "https://" + "api." + "vk.com" + "/method/" + "wall.post" + "?v=5.131&access_token=" + cleanToken;

            // Если картинка загрузилась, прикрепляем её через параметр attachments
            string postDataBody = "owner_id=-" + targetGroupId + "&message=" + Uri.EscapeDataString(finalPostText);
            if (!string.IsNullOrEmpty(attachmentsId))
            {
                postDataBody = postDataBody + "&attachments=" + attachmentsId;
            }

            var content = new StringContent(postDataBody, Encoding.UTF8, "application/x-www-form-urlencoded");

            // Отключаем chunked-кодирование .NET 8 через ContentLength
            byte[] bodyBytes = Encoding.UTF8.GetBytes(postDataBody);
            content.Headers.ContentLength = bodyBytes.Length;

            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(requestUri)) { Content = content };
            var wallResponse = await isolatedClient.SendAsync(request);

            byte[] responseBytes = await wallResponse.Content.ReadAsByteArrayAsync();
            string wallResultString = Encoding.UTF8.GetString(responseBytes).Trim();
            wallResultString = wallResultString.Trim(new char[] { '\uFEFF', '\u200B', ' ', '\n', '\r', '\t' });

            // Дамп-лог для подстраховки
            string debugFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vk_error_page.html");
            await System.IO.File.WriteAllTextAsync(debugFilePath, wallResultString, Encoding.UTF8);

            if (wallResultString.StartsWith("<"))
            {
                throw new Exception("ВК вернул HTML-страницу веб-сайта вместо программного JSON-ответа.");
            }

            using var wallDoc = JsonDocument.Parse(wallResultString);

            if (wallDoc.RootElement.TryGetProperty("error", out var wallErrorEl))
            {
                string errMsg = wallErrorEl.GetProperty("error_msg").GetString() ?? "Unknown Error";
                int errCode = wallErrorEl.GetProperty("error_code").GetInt32();
                throw new Exception($"[Код {errCode}]: {errMsg}\n\nПолный ответ сервера:\n{wallResultString}");
            }

            if (wallDoc.RootElement.TryGetProperty("response", out var responseEl))
            {
                _logger.LogInformation("✅ [VK] Иллюстрированный пост успешно опубликован на стену паблика " + cleanGroup + "!");
                return true;
            }

            throw new Exception("Неизвестный формат ответа от ВКонтакте: " + wallResultString);
        }
        catch (Exception ex)
        {
            _logger.LogError("❌ [VK] Ошибка публикации: " + ex.Message);
            throw;
        }
    }

}
