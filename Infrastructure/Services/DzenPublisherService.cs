using AiSiteFiller.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System.Text.RegularExpressions;
using Keys = OpenQA.Selenium.Keys;

namespace AiSiteFiller.Infrastructure.Services;

public class DzenPublisherService : IPublisherService, IDisposable
{
    private readonly string _sessionId;
    private readonly ILogger<DzenPublisherService> _logger;
    private IWebDriver? _driver;
    public string PlatformName => "DZEN";

    public DzenPublisherService(IConfiguration configuration, ILogger<DzenPublisherService> logger)
    {
        // Для логгера используем общую фабрику
        _logger = logger;
        _sessionId = (configuration["DzenOptions:SessionId"] ?? "").Trim();
    }

    public bool IsCookieExpiringSoon(IConfiguration configuration)
    {
        string dateStr = configuration["DzenOptions:ExpirationDate"] ?? "";
        if (DateTime.TryParse(dateStr, out DateTime expirationDate))
        {
            if ((expirationDate - DateTime.Now).TotalDays <= 7)
            {
                _logger.LogWarning("[DZEN] ВНИМАНИЕ: Срок действия Session_id истекает менее чем через неделю ({0})!", dateStr);

                System.Windows.Forms.MessageBox.Show(
                    "Внимание! Срок действия сессии Дзена (Session_id) истекает " + dateStr + ".\n" +
                    "Пожалуйста, обновите куку в конфигурационном файле, чтобы конвейер не остановился.",
                    "Предупреждение конвейера Дзен",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning
                );
                return true;
            }
        }
        else
        {
            _logger.LogWarning("[DZEN] Не удалось распарсить дату просрочки 'DzenOptions:ExpirationDate'. Проверьте формат (ГГГГ-ММ-ДД).");
        }
        return false;
    }

    public async Task<bool> PublishAsync(string title, string contentHtml, string metadata, string siteId, byte[]? imageBytes)
    {
        // ХАРДКОД / КОНФИГ: Задаем ID вашего канала Дзена из личного кабинета
        string dzenChannelId = "6a5630b88e35e707df2d4ee3";

        try
        {
            // 1. НАСТРОЙКА ИЗОЛИРОВАННОГО ПРОФИЛЯ
            var options = new ChromeOptions();
            options.AddArgument("--start-maximized");
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-dev-shm-usage");

            string dzenProfileDir = "D:\\" + "Repositories\\" + "AiSiteFiller\\" + "DzenWebDriverProfile";
            if (!Directory.Exists(dzenProfileDir))
            {
                Directory.CreateDirectory(dzenProfileDir);
            }
            options.AddArgument("--user-data-dir=" + dzenProfileDir);
            options.AddArgument("--disable-extensions");

            _driver = new ChromeDriver(options);
            _logger.LogInformation("[DZEN] Постоянный браузер конвейера Дзен успешно запущен.");

            // 2. ПРОВЕРКА АВТОРИЗАЦИИ НА ГЛАВНОЙ СТРАНИЦЕ
            string baseDzenUrl = "https://" + "dzen." + "ru";
            _driver.Navigate().GoToUrl(baseDzenUrl);
            await Task.Delay(3000);

            bool isAuthRequired = false;
            try
            {
                var loginButton = _driver.FindElement(By.XPath("//button[contains(., 'Войти') or contains(@class, 'login')]"));
                if (loginButton != null && loginButton.Displayed) isAuthRequired = true;
            }
            catch { }

            if (isAuthRequired)
            {
                _logger.LogWarning("[DZEN] ⚠️ ТРЕБУЕТСЯ АВТОРИЗАЦИЯ! Подтвердите вход в окне браузера...");

                System.Windows.Forms.MessageBox.Show(
                    "Пожалуйста, авторизуйтесь под своим аккаунтом в окне Chrome.\n\n" +
                    "После успешного входа нажмите ОК в этом окне!",
                    "Авторизация в Дзен",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information
                );
                await Task.Delay(3000);
            }

            // ========================================================
            // ШАГ 3: ПЕРЕХОД В ВАШУ ЛИЧНУЮ СТУДИЮ ПО ХАРДКОД-ССЫЛКЕ
            // ========================================================
            // Собираем точную ссылку на вашу Студию через конкатенацию строк
            string studioUrl = "https://" + "dzen." + "ru/" + "profile/" + "editor/" + "id/" + dzenChannelId + "/" + "publications";
            _driver.Navigate().GoToUrl(studioUrl);
            _logger.LogInformation("[DZEN] Открываю вашу личную Студию: " + studioUrl);

            var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));
            await Task.Delay(4000); // Даем интерфейсу Студии полностью отрендериться

            // ========================================================
            // ШАГ 4: НАЖАТИЕ КНОПКИ ПЛЮСИКА ЧЕРЕЗ DATA-TESTID
            // ========================================================
            _logger.LogInformation("[DZEN] Ищу кнопку плюсика добавления публикации...");
            IWebElement addButton = wait.Until(d => d.FindElement(By.XPath(
                "//button[@data-testid='add-publication-button'] " +
                "| //button[contains(@class, 'addButton')]"
            )));

            addButton.Click();
            _logger.LogInformation("[DZEN] Кнопка плюсика успешно нажата. Открылось меню контента.");
            await Task.Delay(1500);

            // ========================================================
            // ШАГ 5: ВЫБОР ПУНКТА «НАПИСАТЬ СТАТЬЮ» ЧЕРЕЗ ARIA-LABEL
            // ========================================================
            _logger.LogInformation("[DZEN] Ищу пункт меню 'Написать статью'...");
            IWebElement writeArticleOption = wait.Until(d => d.FindElement(By.XPath(
                "//*[@aria-label='Написать статью'] " +
                "| //span[text()='Написать статью'] " +
                "| //*[contains(text(), 'Написать статью')]"
            )));

            writeArticleOption.Click();
            _logger.LogInformation("[DZEN] Клик по кнопке 'Написать статью' выполнен!");

            // Ждем, пока Дзен завершит внутреннюю переадресацию и откроет бланк /edit
            _logger.LogInformation("[DZEN] Ожидаю загрузку бланка редактора черновика...");
            await Task.Delay(6000);

            // Передаем управление во вторую часть для заполнения заголовков и текста
            return await CompletePublication(title, contentHtml, metadata, imageBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError("[DZEN] Критическая ошибка конвейера: " + ex.Message);
            return false;
        }
        finally
        {
            CloseBrowser();
        }
    }
    // ========================================================
    // ЧАСТЬ 2: БЕЗОПАСНОЕ ЗАПОЛНЕНИЕ КОНТЕНТА И СБОРКА ЧЕРНОВИКА
    // ========================================================
    private async Task<bool> CompletePublication(string title, string contentHtml, string metadata, byte[]? imageBytes)
    {
        if (_driver == null) return false;

        bool IsGroomingMode = true;
        bool IsDebugDraftMode = true;
        var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(10));

        _logger.LogInformation("[DZEN] Поля Студии открыты. Начинаю пошаговое защищенное заполнение...");

        // --------------------------------------------------------
        // ШАГ 1: ЗАПОЛНЕНИЕ ЗАГОЛОВКА
        // --------------------------------------------------------
        try
        {
            IWebElement titleField = wait.Until(d => d.FindElement(By.XPath(
                "//div[@contenteditable='true' and @aria-describedby='placeholder-63kt4'] " +
                "| //div[contains(@class, 'titleInput')]//div[@contenteditable='true']"
            )));

            titleField.Click();
            await Task.Delay(500);
            titleField.SendKeys(title);
            _logger.LogInformation("[DZEN] Заголовок статьи успешно заполнен.");

            titleField.SendKeys(Keys.Tab);
            await Task.Delay(1000);
        }
        catch (Exception ex)
        {
            _logger.LogError("[DZEN] ❌ Критическая ошибка: Не удалось заполнить заголовок: " + ex.Message);
            return false;
        }

        // --------------------------------------------------------
        // ШАГ 2: ВВОД ОСНОВНОГО ТЕКСТА
        // --------------------------------------------------------
        try
        {
            string cleanText = Regex.Replace(contentHtml.Replace("<p>", "\n\n").Replace("<br>", "\n"), @"<[^>]*>", "");
            if (IsGroomingMode)
            {
                _logger.LogInformation("[DZEN] Включен режим прогрева канала. Удаляю CPA-ссылки...");
                cleanText = Regex.Replace(cleanText, @"https?://[^\s]+", "[информация доступна в источнике]");
            }

            IWebElement bodyField = wait.Until(d => d.FindElement(By.XPath(
                "//div[@contenteditable='true' and @aria-describedby='placeholder-ZenDraftEditor'] " +
                "| //div[contains(@class, 'zenDraftEditor')]//div[@contenteditable='true']"
            )));

            bodyField.Click();
            await Task.Delay(500);
            bodyField.SendKeys(cleanText);
            _logger.LogInformation("[DZEN] Основной текст статьи успешно вставлен.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DZEN] ⚠️ Предупреждение: Не удалось автоматически вставить текст статьи: " + ex.Message);
        }

        // --------------------------------------------------------
        // ШАГ 3: ЗАГРУЗКА ИЗОБРАЖЕНИЯ ЧЕРЕЗ БОКОВУЮ ТЫКАЛКУ
        // --------------------------------------------------------
        if (imageBytes?.Length > 0)
        {
            try
            {
                _logger.LogInformation("[DZEN] Обнаружены байты изображения. Инициирую загрузку файла...");

                string tempImagePath = Path.Combine(Path.GetTempPath(), "dzen_cover_" + Guid.NewGuid().ToString("N") + ".jpg");
                await File.WriteAllBytesAsync(tempImagePath, imageBytes);

                IWebElement currentBody = _driver.FindElement(By.XPath(
                    "//div[@contenteditable='true' and @aria-describedby='placeholder-ZenDraftEditor'] " +
                    "| //div[contains(@class, 'zenDraftEditor')]//div[@contenteditable='true']"
                ));

                // Создаем пустую строку сверху
                currentBody.SendKeys(Keys.Control + Keys.Home);
                await Task.Delay(500);
                currentBody.SendKeys(Keys.Enter);
                await Task.Delay(1000);
                currentBody.SendKeys(Keys.ArrowUp);
                await Task.Delay(1500);

                // Кликаем по кнопке фотоаппарата по вашему data-tip атрибуту
                _logger.LogInformation("[DZEN] Ищу боковую кнопку фотоаппарата на пустой строке...");
                IWebElement imageButton = wait.Until(d => d.FindElement(By.XPath(
                    "//button[@data-tip='Вставить изображение'] " +
                    "| //button[contains(@class, 'sideButton')]"
                )));

                imageButton.Click();
                _logger.LogInformation("[DZEN] Боковая кнопка фотоаппарата успешно нажата!");
                await Task.Delay(1500);

                var fileInput = _driver.FindElement(By.XPath("//input[@type='file' and (contains(@accept, 'image') or contains(@class, 'file'))]"));
                fileInput.SendKeys(tempImagePath);

                _logger.LogInformation("[DZEN] Путь к файлу отправлен в инпут Дзена. Ожидаю обработку обложки...");
                await Task.Delay(7000);

                try { File.Delete(tempImagePath); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[DZEN] ⚠️ Предупреждение: Не удалось загрузить картинку-обложку: " + ex.Message);
            }
        }

        // --------------------------------------------------------
        // ШАГ 4: НАЖАТИЕ ВЕРХНЕЙ КНОПКИ «ОПУБЛИКОВАТЬ»
        // --------------------------------------------------------
        try
        {
            _logger.LogInformation("[DZEN] Нажимаю верхнюю кнопку 'Опубликовать' для вызова шторки настроек...");
            IWebElement publishButton = wait.Until(d => d.FindElement(By.XPath(
                "//button[@data-testid='article-publish-btn']"
            )));
            publishButton.Click();
            await Task.Delay(3000);
        }
        catch (Exception ex)
        {
            _logger.LogError("[DZEN] ❌ Критическая ошибка: Не удалось нажать верхнюю кнопку 'Опубликовать': " + ex.Message);
            return false;
        }

        // --------------------------------------------------------
        // ШАГ 5: ВКЛЮЧЕНИЕ ЧЕКБОКСА ОТЛОЖЕННОЙ ПУБЛИКАЦИИ
        // --------------------------------------------------------
        if (IsDebugDraftMode)
        {
            try
            {
                _logger.LogInformation("[DZEN] Режим отладки активен. Включаю чекбокс 'Опубликовать позже'...");
                IWebElement laterCheckbox = _driver.FindElement(By.XPath(
                    "//span[contains(@class, 'checkbox-input__title') and text()='Опубликовать позже']/ancestor::label//input[@type='checkbox'] " +
                    "| //span[text()='Опубликовать позже']/preceding-sibling::div//input " +
                    "| //label[contains(@class, 'checkbox-input')]//span[contains(., 'Опубликовать позже')]"
                ));
                laterCheckbox.Click();
                _logger.LogInformation("[DZEN] Чекбокс отложенной публикации успешно активирован.");
                await Task.Delay(1000);
            }
            catch (Exception cbEx)
            {
                _logger.LogWarning("[DZEN] ⚠️ Предупреждение: Не удалось активировать чекбокс отложенной публикации: " + cbEx.Message);
            }
        }

        // --------------------------------------------------------
        // ШАГ 6: НАЖАТИЕ ФИНАЛЬНОЙ КНОПКИ ПОДТВЕРЖДЕНИЯ (ПО ВЕРСТКЕ)
        // --------------------------------------------------------
        try
        {
            _logger.LogInformation("[DZEN] Нажимаю финальную кнопку подтверждения внизу шторки...");

            // Локатор бьет точно в предоставленный вами data-testid="publish-btn"
            IWebElement finalConfirmButton = wait.Until(d => d.FindElement(By.XPath(
                "//button[@data-testid='publish-btn'] " +
                "| //div[contains(@class, 'modal-actions')]//button"
            )));

            finalConfirmButton.Click();
            _logger.LogInformation("✅ [DZEN] Статья успешно сохранена в раздел 'Отложенные' (Черновики)!");

            await Task.Delay(4000);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("[DZEN] ❌ Критическая ошибка: Не удалось подтвердить финальную публикацию: " + ex.Message);
            return false;
        }
    }


    private void CloseBrowser()
    {
        if (_driver != null)
        {
            try
            {
                _driver.Quit();
                _driver.Dispose();
                _driver = null;
                _logger.LogInformation("[DZEN] Фоновый процесс Chrome успешно завершен.");
            }
            catch { }
        }
    }

    public void Dispose()
    {
        CloseBrowser();
    }
}
