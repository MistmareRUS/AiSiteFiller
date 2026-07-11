namespace AiSiteFiller.Domain.Constants;

public static class AppCategories
{
    // 1. Строгие текстовые коды для использования в локальной базе данных Postgres
    public const string Smartphones = "smartphones";
    public const string Audio = "audio";
    public const string SmartHome = "smart-home";
    public const string Gadgets = "gadgets";

    // 2. Карта человекочитаемых названий для красивого вывода в консоль
    private static readonly Dictionary<string, string> DisplayNames = new()
    {
        { Smartphones, "📱 Смартфоны и связь" },
        { Audio, "🎧 Аудио и наушники" },
        { SmartHome, "🏠 Умный дом и быт" },
        { Gadgets, "⌚ Умные гаджеты и аксессуары" }
    };

    // 3. КРИТИЧЕСКИЙ МОМЕНТ: Карта соответствия кодов категорий и реальных ID в WordPress.
    // Значения (цифры 2, 3, 4, 5 приведены для примера. Мы заменим их на ваши реальные ID, когда получим их ниже).
    private static readonly Dictionary<string, int> WordPressCategoryIds = new()
    {
        { SmartHome, 2 },
        { Smartphones, 3 },
        { Audio, 4 },
        { Gadgets, 5 }
    };

    public static string GetDisplayName(string categoryCode)
    {
        return DisplayNames.TryGetValue(categoryCode, out var name) ? name : $"📁 {categoryCode}";
    }

    /// <summary>
    /// Возвращает числовой ID категории в WordPress по её текстовому коду из базы данных.
    /// Если соответствие не найдено, возвращает null (пост опубликуется в категорию по умолчанию).
    /// </summary>
    public static int? GetWordPressId(string categoryCode)
    {
        return WordPressCategoryIds.TryGetValue(categoryCode, out var id) ? id : null;
    }
}
