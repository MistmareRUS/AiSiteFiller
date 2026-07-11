namespace AiSiteFiller.Domain.Entities;

public class ArticleTask
{
    public int Id { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public Enums.TaskStatus Status { get; set; } = Enums.TaskStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
