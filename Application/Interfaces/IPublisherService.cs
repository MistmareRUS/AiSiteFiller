using System.Threading.Tasks;

namespace AiSiteFiller.Application.Interfaces;

public interface IPublisherService
{
    // Универсальный метод для отправки куда угодно (на сайт, в ВК, в ТГ)
    Task<bool> PublishAsync(string title, string contentHtml, string metadata, string siteId);
}
