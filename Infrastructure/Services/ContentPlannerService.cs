using AiSiteFiller.Application.Interfaces;
using AiSiteFiller.Domain.Entities;
using AiSiteFiller.Infrastructure.Data;
using System.Text.RegularExpressions;
using TaskStatus = AiSiteFiller.Domain.Enums.TaskStatus;

namespace AiSiteFiller.Infrastructure.Services;

public class ContentPlannerService : IContentPlannerService
{
    private readonly IAiService _aiService;

    public ContentPlannerService(IAiService aiService)
    {
        _aiService = aiService;
    }

    public async Task<int> PopulateQueueWithTrendingTopicsAsync(string categoryCode, int count)
    {
        // Строгий промпт для сбора семантического ядра в ASCII-безопасном режиме
        string prompt = $@"
        Ты ведущий SEO-специалист и аналитик поисковых запросов в Яндексе и Google.
        Придумай ровно {count} уникальных, высокочастотных и самых запрашиваемых названий статей для контентного сайта в {DateTime.Now.Year} году.
        Тематика (категория сайта): '{categoryCode}'.
        
        Формат заголовков:
        - Рейтинги (например: Топ-10 лучших роботов-пылесосов до 40 тысяч рублей...)
        - Сравнения (например: Что лучше купить: iPhone 16 или Samsung S25...)
        - Подробные инструкции (например: Как правильно настроить умный дом Яндекс...)
        
        Требования к ответу:
        Верни ответ СТРОГО в виде простого текстового списка. Каждая тема должна быть на новой строчке. 
        НЕ используй цифры, точки, дефисы, маркеры списков или кавычки. Просто голый текст тем без лишних приветствий и пояснений.";

        try
        {
            string rawTopicsList = await _aiService.GenerateArticleAsync(prompt);

            // Разрезаем текст на строчки, очищаем от мусора и ЖЕСТКО ВЫРЕЗАЕМ ЛЮБОЙ HTML
            string[] topics = rawTopicsList
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                // 1. Вырезаем любые HTML теги (например, <p>, <h2>, </h3>) с помощью регулярного выражения
                .Select(t => Regex.Replace(t, @"<[^>]*>", ""))
                // 2. Убираем случайные маркеры списков и кавычки на концах
                .Select(t => Regex.Replace(t, @"^[0-9\-\.\*\s]+", ""))
                .Select(t => t.Trim('"', ' ', '\''))
                .Where(t => t.Length > 15)
                .Take(count)
                .ToArray();

            if (topics.Length == 0)
            {
                return 0;
            }

            using var db = new AppDbContext();

            // Формируем пачку задач для PostgreSQL
            var newTasks = topics.Select(topic => new ArticleTask
            {
                Topic = topic,
                Category = categoryCode,
                SiteId = "tech-info", // Жестко привязываем к нашему первому сайту
                Status = TaskStatus.Pending, // Задачи сразу встают в активную очередь
                CreatedAt = DateTime.UtcNow
            }).ToList();

            await db.ArticlesQueue.AddRangeAsync(newTasks);
            await db.SaveChangesAsync();

            return newTasks.Count;
        }
        catch (Exception)
        {
            // Ошибка логируется на уровне ИИ-сервиса
            return 0;
        }
    }
}
