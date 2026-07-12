using System.Threading.Tasks;

namespace AiSiteFiller.Application.Interfaces;

public interface IFileStorageService
{
    // Принимает массив байт картинки и имя файла, возвращает уникальный ID или путь в хранилище
    Task<string> SaveFileAsync(byte[] fileBytes, string fileName, string contentType);
    Task<byte[]> GetFileAsync(string id);
}
