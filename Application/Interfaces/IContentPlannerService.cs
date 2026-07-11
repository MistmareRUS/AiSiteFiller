namespace AiSiteFiller.Application.Interfaces;

public interface IContentPlannerService
{
    /// <summary>
    /// Генерирует пачку крутых SEO-тем для указанной категории и сохраняет их в БД
    /// </summary>
    Task<int> PopulateQueueWithTrendingTopicsAsync(string categoryCode, int count);
}
