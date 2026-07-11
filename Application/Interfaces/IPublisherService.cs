using System.Threading.Tasks;

namespace AiSiteFiller.Application.Interfaces;

public interface IPublisherService
{
    Task<bool> PublishAsync(string title, string contentHtml, string metadata, string siteId, string imageUrl);
}
