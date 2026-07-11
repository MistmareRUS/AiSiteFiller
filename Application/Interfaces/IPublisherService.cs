namespace AiSiteFiller.Application.Interfaces;

public interface IPublisherService
{
    Task<bool> PublishAsync(string title, string contentHtml, string category, string siteId);
}
