namespace AiSiteFiller.Application.Interfaces;

public interface IAiService
{
    Task<string> GenerateArticleAsync(string topic);
}
