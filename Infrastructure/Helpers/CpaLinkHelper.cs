using System;
using System.Text.RegularExpressions;

namespace AiSiteFiller.Application.Helpers;

public static class CpaLinkHelper
{
    public static string ReplacePlaceholdersWithSmartLinks(string htmlContent, string articleTitle, string cpaClid)
    {
        if (string.IsNullOrEmpty(htmlContent)) return htmlContent;

        string startMarker = "[CPA_LINK_PLACEHOLDER:|||";
        string endMarker = "|||]";
        string safeClid = string.IsNullOrEmpty(cpaClid) ? "" : cpaClid;

        while (htmlContent.Contains(startMarker))
        {
            int startIndex = htmlContent.IndexOf(startMarker);
            int endMarkerIndex = htmlContent.IndexOf(endMarker, startIndex);

            if (endMarkerIndex == -1) break;

            // Вырезаем то, что находится СТРОГО между ||| и |||
            int nameStart = startIndex + startMarker.Length;
            int nameLength = endMarkerIndex - nameStart;
            string productName = htmlContent.Substring(nameStart, nameLength).Trim();
            // Атомарная сборка URL по README.AI.md
            string protocol = "https://" + "market.yandex.ru";
            string path = "/search?text=";
            string queryParam = Uri.EscapeDataString(productName);
            string affiliateParam = "&clid=" + safeClid;

            string smartAffiliateUrl = protocol + path + queryParam + affiliateParam;

            // Вычисляем полную длину всей заглушки, включая [ и ]
            int totalLength = (endMarkerIndex + endMarker.Length) - startIndex;
            string fullMarker = htmlContent.Substring(startIndex, totalLength);

            // Заменяем сложный уникальный маркер на чистую ссылку
            htmlContent = htmlContent.Replace(fullMarker, smartAffiliateUrl);
        }

        return htmlContent;
    }

    public static string GenerateMaskedVkLink(string articleTitle, string siteId, string cpaClid)
    {
        // 1. Очищаем тему от мусорных SEO-слов
        string searchQuery = articleTitle;
        searchQuery = Regex.Replace(searchQuery, @"(?i)(сравнение|лучших|обзор|тест|в 2026 году|против|vs|какую выбрать|рейтинг|отзывы|характеристики)", " ");
        searchQuery = Regex.Replace(searchQuery, @"\s+", " ").Trim();

        var words = searchQuery.Split(' ');
        if (words.Length > 4)
        {
            searchQuery = words[0] + "-" + words[1] + "-" + words[2] + "-" + words[3];
        }
        else
        {
            searchQuery = searchQuery.Replace(" ", "-");
        }

        // Переводим название в латиницу (или используем очищенную строку, переведя в ловеркейс)
        string cleanSlug = Uri.EscapeDataString(searchQuery.ToLower());

        // 2. СОБИРАЕМ КРАСИВЫЙ РЕДИРЕКТ-URL НА ВАШЕМ ПОДДОМЕНЕ ПО ДОГОВОРУ КОНКАТЕНАЦИИ
        // Ссылка будет вести на ваш сайт в специальную папку перенаправлений /go/
        string maskedUrl = "https://" + siteId + ".mistmare.ru" + "/go/" + cleanSlug +"? clid = " + cpaClid;

        return maskedUrl;
    }
}
