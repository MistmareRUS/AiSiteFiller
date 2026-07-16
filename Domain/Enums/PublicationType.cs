namespace AiSiteFiller.Domain.Enums;

public enum PublicationType
{
    /// <summary>
    /// Автономные SEO-платформы (публикация полной статьи с HTML)
    /// </summary>
    FullSeoArticle = 0,

    /// <summary>
    /// Соцсети и мессенджеры (публикация только короткого анонса со ссылкой)
    /// </summary>
    AnnouncementOnly = 10
}
