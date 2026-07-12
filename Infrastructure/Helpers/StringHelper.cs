using System.Text.RegularExpressions;

namespace AiSiteFiller.Infrastructure.Helpers;

public static class StringHelper
{
    public static string GenerateSlug(string phrase)
    {
        string str = phrase.ToLower().Trim();

        // Массив для точечной транслитерации русского алфавита
        string[] rus = { "а", "б", "в", "г", "д", "е", "ё", "ж", "з", "и", "й", "к", "л", "м", "н", "о", "п", "р", "с", "т", "у", "ф", "х", "ц", "ч", "ш", "щ", "ъ", "ы", "ь", "э", "ю", "я" };
        string[] lat = { "a", "b", "v", "g", "d", "e", "e", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "kh", "ts", "ch", "sh", "shch", "", "y", "", "e", "yu", "ya" };

        for (int i = 0; i < rus.Length; i++)
        {
            str = str.Replace(rus[i], lat[i]);
        }

        // Очищаем от спецсимволов и форматируем пробелы в дефисы
        str = Regex.Replace(str, @"[^a-z0-9\s-]", "");
        str = Regex.Replace(str, @"\s+", " ").Trim();
        str = str.Replace(" ", "-");

        return str;
    }
}
