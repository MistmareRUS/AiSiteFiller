using AiSiteFiller.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text;

namespace AiSiteFiller.Infrastructure.Services
{
    public class TelegramPublisherService : IPublisherService
    {
        private readonly ILogger<TelegramPublisherService> _logger;
        private readonly string _bridgeUrl, _bridgeToken, _botToken, _chatId;

        public string PlatformName => "Telegram";

        public TelegramPublisherService(IConfiguration configuration, ILogger<TelegramPublisherService> logger)
        {
            _logger = logger;
            // Вычитываем настройки из appsettings.json
            _bridgeUrl = (configuration["TelegramOptions:BridgeUrl"] ?? string.Empty).Replace("\n", "").Replace("\r", "").Trim();
            _bridgeToken = (configuration["TelegramOptions:BridgeToken"] ?? string.Empty).Replace("\n", "").Replace("\r", "").Trim();
            _botToken = (configuration["TelegramOptions:BotToken"] ?? string.Empty).Replace("\n", "").Replace("\r", "").Trim();
            _chatId = (configuration["TelegramOptions:ChatId"] ?? string.Empty).Replace("\n", "").Replace("\r", "").Trim();
        }

        // Реализация интерфейса (5 параметров)
        public async Task<bool> PublishAsync(string title, string contentHtml, string metadata, string siteId, byte[]? imageBytes)
        {
            if (string.IsNullOrEmpty(_bridgeUrl) || string.IsNullOrEmpty(_botToken)) return false;

            try
            {
                // 1. Формирование текста и ссылки (остается без изменений)
                string formattedText = PrepareHtmlForTelegram(title, contentHtml);
                string maskedCpaUrl = Application.Helpers.CpaLinkHelper.GenerateMaskedVkLink(title, "tech-info");
                string finalPostText = $"🔥 <b>{title.ToUpper()}</b>\n\n{formattedText}\n\n🚀 <a href=\"{maskedCpaUrl}\">Сравнить цены</a>";

                // 2. Кодируем картинку в Base64, если она есть
                string imageBase64 = string.Empty;
                if (imageBytes != null && imageBytes.Length > 0)
                {
                    imageBase64 = Convert.ToBase64String(imageBytes);
                }

                // 3. Отправка через Beget-мост (добавили поле image_base64)
                using var client = new HttpClient();
                var payload = new
                {
                    bot_token = _botToken,
                    chat_id = _chatId,
                    text = finalPostText,
                    image_base64 = imageBase64 // Передаем картинку строкой
                };

                var request = new HttpRequestMessage(HttpMethod.Post, _bridgeUrl);
                request.Headers.Add("X-Bridge-Token", _bridgeToken);
                request.Content = JsonContent.Create(payload);

                var response = await client.SendAsync(request);
                return response.IsSuccessStatusCode;

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка Telegram");
                return false;
            }
        }
        private string PrepareHtmlForTelegram(string title, string contentHtml)
        {
            if (string.IsNullOrEmpty(contentHtml)) return string.Empty;

            // 1. Умный разбор HTML-таблиц в текстовую структуру
            string processed = ConvertHtmlTableToTelegramText(contentHtml);

            // 2. Адаптация стандартных тегов под верстку Telegram API
            processed = processed.Replace("<h2>", "\n\n<b>").Replace("</h2>", "</b>\n")
                                 .Replace("<h3>", "\n\n<b>").Replace("</h3>", "</b>\n")
                                 .Replace("<p>", "\n").Replace("</p>", "\n")
                                 .Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n")
                                 .Replace("<strong>", "<b>").Replace("</strong>", "</b>")
                                 .Replace("<em>", "<i>").Replace("</em>", "</i>");

            // 3. Вырезаем все остальные неподдерживаемые HTML-теги во избежание ошибки 400 Bad Request
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"<(?!/?(b|i|a|u|code|pre)\b)[^>]*>", "");

            // 4. Схлопываем множественные переносы строк
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"\n{3,}", "\n\n");

            return processed.Trim();
        }

        private string ConvertHtmlTableToTelegramText(string htmlInput)
        {
            string processedText = htmlInput;
            try
            {
                var tableMatches = System.Text.RegularExpressions.Regex.Matches(processedText, @"<table[^>]*>([\s\S]*?)<\/table>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

                foreach (System.Text.RegularExpressions.Match tableMatch in tableMatches)
                {
                    var rows = System.Text.RegularExpressions.Regex.Matches(tableMatch.Groups[1].Value, @"<tr[^>]*>([\s\S]*?)<\/tr>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                    if (rows.Count <= 1) continue;

                    var sbTable = new StringBuilder();
                    sbTable.AppendLine("\n📊 <b>ХАРАКТЕРИСТИКИ:</b>");
                    sbTable.AppendLine("───────────────────");

                    var headersMatches = System.Text.RegularExpressions.Regex.Matches(rows[0].Value, @"<t[hd][^>]*>([\s\S]*?)<\/t[hd]>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                    var headers = new System.Collections.Generic.List<string>();
                    foreach (System.Text.RegularExpressions.Match hMatch in headersMatches)
                    {
                        headers.Add(System.Text.RegularExpressions.Regex.Replace(hMatch.Value, @"<[^>]*>", "").Trim());
                    }

                    for (int i = 1; i < rows.Count; i++)
                    {
                        var cells = System.Text.RegularExpressions.Regex.Matches(rows[i].Value, @"<td[^>]*>([\s\S]*?)<\/td>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                        if (cells.Count == 0) continue;

                        string modelName = System.Text.RegularExpressions.Regex.Replace(cells[0].Value, @"<[^>]*>", "").Trim();
                        sbTable.AppendLine($"🔹 <b>{modelName.ToUpper()}</b>");

                        for (int j = 1; j < cells.Count && j < headers.Count; j++)
                        {
                            string cellContent = cells[j].Groups[1].Value.Trim();
                            string value = cellContent;

                            if (cellContent.StartsWith("http") && !cellContent.Contains("<a "))
                            {
                                value = $"<a href=\"{cellContent}\">Купить на Маркете</a>";
                            }
                            else if (!cellContent.Contains("<a "))
                            {
                                value = System.Text.RegularExpressions.Regex.Replace(cellContent, @"<[^>]*>", "").Trim();
                            }

                            sbTable.AppendLine($" • {headers[j]}: {value}");
                        }
                        sbTable.AppendLine();
                    }
                    sbTable.AppendLine("───────────────────");
                    processedText = processedText.Replace(tableMatch.Value, sbTable.ToString());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[TG_TABLE] Ошибка парсинга таблицы: " + ex.Message);
            }
            return processedText;
        }
    }
}
