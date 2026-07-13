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

    public string PlatformName => "VK";

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
            // 1. УМНАЯ КОНВЕРТАЦИЯ HTML-ТАБЛИЦ В СТИЛЬНЫЙ СПИСОК ДЛЯ ВК
            string textWithFormattedTables = contentHtml;

            try
            {
                // Ищем таблицы в тексте статьи
                var tableMatches = System.Text.RegularExpressions.Regex.Matches(textWithFormattedTables, @"<table[^>]*>(.*?)<\/table>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

                foreach (System.Text.RegularExpressions.Match tableMatch in tableMatches)
                {
                    string tableHtml = tableMatch.Groups[1].Value;
                    var rows = System.Text.RegularExpressions.Regex.Matches(tableHtml, @"<tr[^>]*>(.*?)<\/tr>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

                    if (rows.Count > 1)
                    {
                        var sbTable = new StringBuilder();
                        sbTable.AppendLine("\n📊 СРАВНИТЕЛЬНЫЕ ХАРАКТЕРИСТИКИ МОДЕЛЕЙ:");
                        sbTable.AppendLine("───────────────────────────────────");

                        // Парсим заголовки таблицы (первая строка tr)
                        var headersMatches = System.Text.RegularExpressions.Regex.Matches(rows[0].Value, @"<th[^>]*>(.*?)<\/th>|<td[^>]*>(.*?)<\/td>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                        var headers = new System.Collections.Generic.List<string>();
                        foreach (System.Text.RegularExpressions.Match hMatch in headersMatches)
                        {
                            string hText = System.Text.RegularExpressions.Regex.Replace(hMatch.Value, @"<[^>]*>", "").Trim();
                            headers.Add(hText);
                        }

                        // Парсим строки с данными (начиная со второй строки)
                        for (int i = 1; i < rows.Count; i++)
                        {
                            var cells = System.Text.RegularExpressions.Regex.Matches(rows[i].Value, @"<td[^>]*>(.*?)<\/td>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                            if (cells.Count > 0)
                            {
                                string modelName = System.Text.RegularExpressions.Regex.Replace(cells[0].Value, @"<[^>]*>", "").Trim();
                                sbTable.AppendLine("🔹 " + modelName.ToUpper());

                                // Выводим остальные характеристики со сдвигом
                                for (int j = 1; j < cells.Count && j < headers.Count; j++)
                                {
                                    string cellValue = System.Text.RegularExpressions.Regex.Replace(cells[j].Value, @"<[^>]*>", "").Trim();
                                    sbTable.AppendLine("  ▪️ " + headers[j] + ": " + cellValue);
                                }
                                sbTable.AppendLine(); // Разделитель между моделями
                            }
                        }
                        sbTable.AppendLine("───────────────────────────────────");

                        // Заменяем сырой HTML таблицы на наш красивый текстовый блок
                        textWithFormattedTables = textWithFormattedTables.Replace(tableMatch.Value, sbTable.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[VK] Ошибка конвертации таблицы: " + ex.Message);
            }

            // 2. СТАНДАРТНАЯ ОЧИСТКА ОСТАЛЬНОГО HTML-ТЕКСТА
            string cleanText = textWithFormattedTables
                .Replace("<p>", "").Replace("</p>", "\n\n")
                .Replace("<h2>", "🔥 ").Replace("</h2>", " \n")
                .Replace("<h3>", "⚡ ").Replace("</h3>", " \n")
                .Replace("<ul>", "").Replace("</ul>", "")
                .Replace("<li>", "🔸 ").Replace("</li>", "\n")
                .Replace("strong", "").Replace("</strong>", "");

            // Вырезаем все остаточные теги
            cleanText = System.Text.RegularExpressions.Regex.Replace(cleanText, @"<[^>]*>", "");

            // 3. УМНАЯ СРАБОТКА CPA-КОНВЕЙЕРА ДЛЯ ВК: Формируем красивую маскированную ссылку
            string domainId = "tech-info"; // Наш рабочий поддомен
            string maskedCpaUrl = Application.Helpers.CpaLinkHelper.GenerateMaskedVkLink(title, domainId);

            // Склеиваем сочный продающий текст анонса для умной ленты ВК по договору конкатенации
            string finalPostText = "📰 " + title.ToUpper() + "\n\n" +
                                   cleanText + "\n\n" +
                                   "🚀 Читать полный обзор и сравнить актуальные цены: " + maskedCpaUrl;

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

                            // ЖЕЛЕЗНЫЙ ДЕБАГ: Принудительно выбрасываем ошибку с полным сырым ответом ВК,
                            // чтобы этот JSON вывелся на экран во всплывающем MessageBox!
                            //throw new Exception("СЫРОЙ ОТВЕТ ВК (ШАГ В):\n\n" + saveResultStr);

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

            // 2. ФИНАЛЬНАЯ ПУБЛИКАЦИЯ НА СТЕНУ: Вызываем wall.post через конкатенацию
            string requestUri = "https://" + "api." + "vk.com" + "/method/" + "wall.post" + "?v=5.131&access_token=" + cleanToken;

            // ЖЕЛЕЗНЫЙ ФИКС: Обязательно добавляем &from_group=1, чтобы пост вышел от лица паблика, а не от вашего имени!
            string postDataBody = "owner_id=-" + targetGroupId + "&from_group=1&message=" + Uri.EscapeDataString(finalPostText);
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

#if DEBUG            
            // Дамп-лог для подстраховки
            string debugFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vk_error_page.txt");
            await System.IO.File.WriteAllTextAsync(debugFilePath, wallResultString, Encoding.UTF8);
#endif

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
