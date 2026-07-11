using System.Threading.Tasks;

namespace AiSiteFiller.Application.Interfaces;

public interface IAiService
{
    Task<string> GenerateArticleAsync(string topic);

    Task<string> GenerateImageAsync(string topic);
}
