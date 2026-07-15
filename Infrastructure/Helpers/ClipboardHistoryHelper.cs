using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Helpers
{
    public static class ClipboardHistoryHelper
    {
        /// <summary>
        /// Точечно удаляет из панели Win+V строго один последний добавленный элемент,
        /// полностью сохраняя всю остальную историю и скопированные пользователем данные.
        /// </summary>
        public static async Task DeleteLastItemAsync(ILogger logger)
        {
            try
            {
                // Явно указываем пространство имен WinRT, чтобы убрать конфликты с WinForms
                if (!Windows.ApplicationModel.DataTransfer.Clipboard.IsHistoryEnabled()) return;

                logger.LogInformation("[CLIPBOARD_HELPER] Запрашиваю список истории Win+V...");

                var historyResult = await Windows.ApplicationModel.DataTransfer.Clipboard.GetHistoryItemsAsync();

                if (historyResult.Status == Windows.ApplicationModel.DataTransfer.ClipboardHistoryItemsResultStatus.Success)
                {
                    var lastItem = historyResult.Items.FirstOrDefault();

                    if (lastItem != null)
                    {
                        // Удаляем строго этот конкретный элемент из журнала
                        Windows.ApplicationModel.DataTransfer.Clipboard.DeleteItemFromHistory(lastItem);
                        logger.LogInformation("[CLIPBOARD_HELPER] ✅ HTML-блок статьи успешно вырезан из истории. Ваши данные не тронуты.");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("[CLIPBOARD_HELPER] Не удалось точечно удалить элемент из истории: " + ex.Message);
            }
        }
    }
}
