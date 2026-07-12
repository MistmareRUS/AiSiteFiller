using System.Threading.Tasks;

namespace AiSiteFiller.Application.Interfaces;

public interface IPublisherService
{
    string PlatformName { get; }
    Task<bool> PublishAsync(string title, string contentHtml, string metadata, string siteId, byte[]? imageBytes);
}
