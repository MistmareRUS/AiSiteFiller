using AiSiteFiller.Application.Interfaces;
using AiSiteFiller.Domain.Entities;
using AiSiteFiller.Infrastructure.Data;
using TaskStatus = AiSiteFiller.Domain.Enums.TaskStatus;

namespace AiSiteFiller.Infrastructure.Services;

public class ContentPlannerService : IContentPlannerService
{
    private readonly IAiService _aiService;

    // Используем Dependency Injection: для планирования тем нам нужен рабочий сервис ИИ
    public ContentPlannerService(IAiService aiService)
    {
        _aiService = aiService;
    }

    public async Task<int> PopulateQueueWithTrendingTopicsAsync(string categoryCode, int count)
    {
        Console.WriteLine($"\n📦 [ПЛАНИРОВЩИК] Запрашиваю у ИИ {count} горячих SEO-тем для категории: '{categoryCode}'...");

        // Формируем строгий промпт, чтобы ИИ вернул темы строго в формате простого текстового списка
        string prompt = $@"
        Ты эксперт по SEO и анализу поисковых запросов в Яндексе в сфере гаджетов и электроники.
        Придумай ровно {count} уникальных, высокочастотных и актуальных названий статей для сайта в 2026 году.
        Категория сайта: '{categoryCode}'.
        
        Правила для тем:
        - Формат заголовков: Рейтинги (Топ-10...), Инструкции (Как настроить...), Сравнения (Что лучше...), Честные обзоры перед покупкой.
        - Темы должны быть конкретными (с указанием популярных брендов, моделей техники и ценовых сегментов).
        - Ответ верни СТРОГО в виде списка, где каждая тема на новой строчке. Не используй цифры, точки, маркеры дефисов или кавычки. Просто текст тем.";

        try
        {
            // Используем наш существующий OpenAiGptService для генерации списка тем
            string rawTopicsList = await _aiService.GenerateArticleAsync(prompt);

            // Разбиваем полученный текст на строчки и очищаем от пустых элементов
            string[] topics = rawTopicsList
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 10) // Отсекаем слишком короткий мусор
                .Take(count)
                .ToArray();

            if (topics.Length == 0)
            {
                Console.WriteLine("⚠️ [ПЛАНИРОВЩИК] ИИ вернул пустой список или неверный формат.");
                return 0;
            }

            // Записываем новые темы в PostgreSQL через EF Core
            using var db = new AppDbContext();

            var newTasks = topics.Select(topic => new ArticleTask
            {
                Topic = topic,
                Category = categoryCode,
                Status = TaskStatus.Pending, // Новые темы сразу встают в фоновую очередь
                CreatedAt = DateTime.UtcNow
            }).ToList();

            await db.ArticlesQueue.AddRangeAsync(newTasks);
            await db.SaveChangesAsync();

            Console.WriteLine($"✅ [ПЛАНИРОВЩИК] База данных успешно пополнена! Добавлено {newTasks.Count} новых фоновых задач.");
            return newTasks.Count;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ [ПЛАНИРОВЩИК] Ошибка при планировании контента: {ex.Message}");
            return 0;
        }
    }
}
