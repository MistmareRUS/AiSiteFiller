using TaskStatus = AiSiteFiller.Domain.Enums.TaskStatus;

namespace AiSiteFiller.Domain.Entities;

public class ArticleTask
{
    public int Id { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    public string SiteId { get; set; } = string.Empty;

    public TaskStatus Status { get; set; } = TaskStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
