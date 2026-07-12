using TaskStatus = AiSiteFiller.Domain.Enums.TaskStatus;

namespace AiSiteFiller.Domain.Entities;

public class ArticleTask
{
    public int Id { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    public string SiteId { get; set; } = string.Empty;

    public string? ContentHtml { get; set; }
    public string? MongoImageId { get; set; } // Ссылка на файл обложки в MongoDB


    public TaskStatus Status { get; set; } = TaskStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<PublicationTask> PublicationTasks { get; set; } = new List<PublicationTask>();

}
