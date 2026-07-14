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
        try
        {
            // 1. НАСТРОЙКА ЧИСТОГО БРАУЗЕРА CHROME
            var options = new ChromeOptions();
            options.AddArgument("--start-maximized");
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-dev-shm-usage");
            options.AddArgument("--user-agent=" + "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            _driver = new ChromeDriver(options);
            _logger.LogInformation("[DZEN] Экземпляр Chrome запущен для проверки сессии.");

            string baseDzenUrl = "https://" + "dzen." + "ru";
            _driver.Navigate().GoToUrl(baseDzenUrl);
            await Task.Delay(3000);

            // ========================================================
            // ШАГ 2: ПРОВЕРКА CONFIG / РУЧНАЯ АВТОРИЗАЦИЯ -> БУФЕР ОБМЕНА
            // ========================================================
            // Если в конфиге пусто — запускаем ваш полуавтоматический сценарий
            if (string.IsNullOrEmpty(_sessionId))
            {
                _logger.LogWarning("[DZEN] ⚠️ В конфигурации отсутствует SessionId. Ожидаю ручной вход...");

                // Выводим бесконечную модалку WinForms БЕЗ ТАЙМЕРОВ
                System.Windows.Forms.MessageBox.Show(
                    "В конфигурационном файле appsettings.json не найден токен Дзена.\n\n" +
                    "Пожалуйста, прямо сейчас в открывшемся окне браузера выполните вход в аккаунт.\n\n" +
                    "После того как вы успешно авторизуетесь и попадете на главную страницу Дзена, нажмите ОК в этом окне.",
                    "Требуется первая авторизация Дзен",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information
                );

                _logger.LogInformation("[DZEN] Модалка закрыта. Извлекаю свежую куку из браузера...");

                // Вытаскиваем нужную куку zen_session_id из текущей сессии Chrome
                // Вытаскиваем нужную куку zen_session_id из текущей сессии Chrome
                var extractedCookie = _driver.Manage().Cookies.GetCookieNamed("zen_session_id");

                if (extractedCookie != null && !string.IsNullOrEmpty(extractedCookie.Value))
                {
                    string newTokenValue = extractedCookie.Value;

                    // 🔥 ФИКС ОШИБКИ: Запускаем копирование в буфер в принудительном STA-потоке
                    var staThread = new System.Threading.Thread(() =>
                    {
                        try
                        {
                            System.Windows.Forms.Clipboard.SetText(newTokenValue);
                        }
                        catch (Exception clipboardEx)
                        {
                            _logger.LogError("[DZEN] Ошибка записи в буфер обмена: " + clipboardEx.Message);
                        }
                    });

                    staThread.SetApartmentState(System.Threading.ApartmentState.STA); // Задаем поток как STA
                    staThread.Start();
                    staThread.Join(); // Дожидаемся завершения записи в буфер

                    _logger.LogInformation("✅ [DZEN] Свежий токен zen_session_id успешно скопирован в буфер обмена!");

                    System.Windows.Forms.MessageBox.Show(
                        "Токен авторизации успешно скопирован в буфер обмена!\n\n" +
                        "Сейчас приложение закроется. Откройте ваш файл конфигурации appsettings.json и вставьте значение из буфера в поле 'SessionId'.\n\n" +
                        "После этого перезапустите конвейер задач.",
                        "Токен скопирован в буфер",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Warning
                    );
                }
                else
                {
                    _logger.LogError("[DZEN] Ошибка: Не удалось найти куку 'zen_session_id' после авторизации. Вы вошли в аккаунт?");
                }

                // Принудительно гасим браузер и прерываем выполнение задачи, чтобы вы сохранили конфиг
                CloseBrowser();
                return false;
            }

            // ========================================================
            // ШАГ 3: ШТАТНЫЙ АВТОМАТИЧЕСКИЙ ВХОД (Если кука уже есть в конфиге)
            // ========================================================
            _logger.LogInformation("[DZEN] Токен найден в конфигурации. Внедряю сессию...");

            var zenCookie = new Cookie("zen_session_id", _sessionId, "." + "dzen." + "ru", "/", DateTime.Now.AddYears(1));
            _driver.Manage().Cookies.AddCookie(zenCookie);

            _driver.Navigate().Refresh();
            await Task.Delay(4000);

            // ========================================================
            // ШАГ 4: РОУТИНГ В ВАШУ ЛИЧНУЮ СТУДИЮ
            // ========================================================
            string dzenChannelId = "6a5630b88e35e707df2d4ee3"; // Ваш хардкод ID
            string studioUrl = "https://" + "dzen." + "ru/" + "profile/" + "editor/" + "id/" + dzenChannelId + "/" + "publications";

            _driver.Navigate().GoToUrl(studioUrl);
            _logger.LogInformation("[DZEN] Открываю Студию публикаций: " + studioUrl);

            var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));
            await Task.Delay(4000);

            // Нажимаем плюсик по data-testid
            _logger.LogInformation("[DZEN] Нажимаю кнопку плюсика добавления публикации...");
            IWebElement addButton = wait.Until(d => d.FindElement(By.XPath("//button[@data-testid='add-publication-button']")));
            addButton.Click();
            await Task.Delay(1500);

            // Нажимаем "Написать статью" по aria-label
            _logger.LogInformation("[DZEN] Нажимаю пункт меню 'Написать статью'...");
            IWebElement writeArticleOption = wait.Until(d => d.FindElement(By.XPath("//*[@aria-label='Написать статью']")));
            writeArticleOption.Click();

            _logger.LogInformation("[DZEN] Ожидаю загрузку бланка редактора черновика...");
            await Task.Delay(6000);

            // Переходим ко второй части для заполнения полей статьи
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

    private async Task<bool> CompletePublication(string title, string contentHtml, string metadata, byte[]? imageBytes)
    {
        if (_driver == null) return false;

        bool IsGroomingMode = true;
        bool IsDebugDraftMode = true;
        var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(10));

        // --------------------------------------------------------
        // 🔥 ЗАКРЫТИЕ ВСКУЮЩЕЙ ОБУЧАЛКИ ДЗЕНА (ReactModal)
        // --------------------------------------------------------
        try
        {
            _logger.LogInformation("[DZEN] Проверяю, не перекрыта ли страница окном обучения...");
            // Задаем короткое ожидание в 3 секунды, чтобы не затягивать процесс
            var waitHelp = new WebDriverWait(_driver, TimeSpan.FromSeconds(3));

            // Ищем крестик по вашему точному атрибуту aria-label="Закрыть" и классам из файла task.txt
            IWebElement closeHelpButton = waitHelp.Until(d => d.FindElement(By.XPath(
                "//div[contains(@class, 'help-popup')]//div[@aria-label='Закрыть' and @role='button'] " +
                "| //div[contains(@class, 'closeCross') and @aria-label='Закрыть'] " +
                "| //div[@aria-label='Закрыть' and contains(@class, 'help-popup')]"
            )));

            closeHelpButton.Click();
            _logger.LogInformation("[DZEN] Окно обучения Дзена успешно закрыто крестиком.");
            await Task.Delay(1000); // Даем модалке исчезнуть из DOM
        }
        catch (Exception)
        {
            // Если окна обучения не было — это штатная ситуация, просто идем дальше
            _logger.LogInformation("[DZEN] Окно обучения отсутствует, доступ к редактору свободен.");
        }

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
            await Task.Delay(1200);
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
        // ШАГ 3: ЗАГРУЗКА ИЗОБРАЖЕНИЯ (ЖЕЛЕЗОБЕТОННЫЙ ВАРИАНТ ЧЕРЕЗ JS)
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

                // 1. Имитируем клик в редактор и жестко прыгаем в самый верх текста
                currentBody.Click();
                await Task.Delay(500);
                currentBody.SendKeys(Keys.Control + Keys.Home);
                await Task.Delay(500);

                // 2. Создаем пустую строку сверху
                currentBody.SendKeys(Keys.Enter);
                await Task.Delay(1000);

                // 🔥 МАНЕВР: Переводим курсор строго на созданную ПЕРВУЮ пустую строку
                currentBody.SendKeys(Keys.Control + Keys.Home);
                await Task.Delay(1500); // Даем Дзену отобразить боковую панель

                // 3. Используем JavaScript, чтобы вытащить скрытую кнопку, даже если Дзен не до конца отобразил её анимацию
                _logger.LogInformation("[DZEN] Ищу боковую кнопку фотоаппарата через JS...");

                IJavaScriptExecutor js = (IJavaScriptExecutor)_driver;

                // Выполняем JS-скрипт, который найдет кнопку с вашим data-tip и принудительно нажмет на неё
                js.ExecuteScript(
                    "var btn = document.querySelector(\"button[data-tip='Вставить изображение']\") " +
                    "|| document.querySelector(\".article-editor-desktop--side-button__sideButton-1z\"); " +
                    "if(btn) { btn.click(); } else { console.log('Кнопка не найдена в DOM'); }"
                );

                _logger.LogInformation("[DZEN] Скрипт клика по боковой кнопке фотоаппарата выполнен.");
                await Task.Delay(2000);

                // 4. Передаем путь к файлу в инпут, который создался после клика
                var fileInput = wait.Until(d => d.FindElement(By.XPath(
                    "//input[@type='file' and (contains(@accept, 'image') or contains(@class, 'file'))]"
                )));

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
