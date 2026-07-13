using System;
using System.Text.RegularExpressions;

namespace AiSiteFiller.Application.Helpers;

public static class CpaLinkHelper
{
    public static string ReplacePlaceholdersWithSmartLinks(string htmlContent, string articleTitle, string cpaClid)
    {
        if (string.IsNullOrEmpty(htmlContent) || !htmlContent.Contains("[CPA_LINK_PLACEHOLDER]"))
        {
            return htmlContent;
        }

        // Если clid забыли прописать в конфиге, подставляем пустую строку, чтобы ссылка не ломалась
        string safeClid = string.IsNullOrEmpty(cpaClid) ? "" : cpaClid;

        // 1. Очищаем тему от мусорных SEO-слов
        string searchQuery = articleTitle;
        searchQuery = Regex.Replace(searchQuery, @"(?i)(сравнение|лучших|обзор|тест|в 2026 году|против|vs|какую выбрать|рейтинг|отзывы|характеристики)", " ");
        searchQuery = Regex.Replace(searchQuery, @"\s+", " ").Trim();

        var words = searchQuery.Split(' ');
        if (words.Length > 4)
        {
            searchQuery = words[0] + " " + words[1] + " " + words[2] + " " + words[3];
        }

        string encodedQuery = Uri.EscapeDataString(searchQuery);

        // 2. СОБИРАЕМ CPA-ДИПЛИНК С ДИНАМИЧЕСКИМ CLID ПО ДОГОВОРУ КОНКАТЕНАЦИИ
        string smartAffiliateUrl = "https://" + "market.yandex.ru" + "/search" + "?text=" + encodedQuery + "&clid=" + safeClid;

        // 3. Заменяем заглушки
        string finalizedHtml = htmlContent.Replace("[CPA_LINK_PLACEHOLDER]", smartAffiliateUrl);

        return finalizedHtml;
    }

    // Метод генерирует маскированную ссылку для постов ВКонтакте
    public static string GenerateMaskedVkLink(string articleTitle, string siteId)
    {
        // 1. Очищаем тему от мусорных SEO-слов, как в прошлый раз
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
        string cleanSlug = searchQuery.ToLower();

        // 2. СОБИРАЕМ КРАСИВЫЙ РЕДИРЕКТ-URL НА ВАШЕМ ПОДДОМЕНЕ ПО ДОГОВОРУ КОНКАТЕНАЦИИ
        // Ссылка будет вести на ваш сайт в специальную папку перенаправлений /go/
        string maskedUrl = "https://" + siteId + ".mistmare.ru" + "/go/" + cleanSlug;

        return maskedUrl;
    }
}
