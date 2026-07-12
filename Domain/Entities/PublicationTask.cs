using TaskStatus = AiSiteFiller.Domain.Enums.TaskStatus; // Убедитесь, что этот namespace соответствует вашему проекту

namespace AiSiteFiller.Domain.Entities;

public class PublicationTask
{
    public int Id { get; set; }

    // Ссылка на родительскую статью
    public int ArticleTaskId { get; set; }
    public virtual ArticleTask ArticleTask { get; set; } = null!;

    // Название платформы (например: "WordPress", "VK", "Telegram")
    public string Platform { get; set; } = string.Empty;

    // Независимый статус конкретно этой публикации
    public TaskStatus Status { get; set; } = TaskStatus.Pending;

    // Дополнительное поле для хранения внешней ссылки или ID поста после публикации
    public string? ExternalId { get; set; }

    public DateTime? ProcessedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
