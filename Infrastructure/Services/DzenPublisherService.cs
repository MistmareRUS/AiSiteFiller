using AiSiteFiller.Application.Interfaces;
using AiSiteFiller.Domain.Enums;
using Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System.Text;
using System.Text.RegularExpressions;
using Keys = OpenQA.Selenium.Keys;

namespace AiSiteFiller.Infrastructure.Services;

public class DzenPublisherService : IPublisherService, IDisposable
{
    private readonly string _dzenChannelId;
    private readonly ILogger<DzenPublisherService> _logger;
    private readonly bool _isGroomingMode = true;
    private readonly bool _isDebugDraftMode = true;
    private IWebDriver? _driver;
    public string PlatformName => "DZEN";
    public PublicationType PublishType => PublicationType.FullSeoArticle;


    public DzenPublisherService(IConfiguration configuration, ILogger<DzenPublisherService> logger)
    {
        // Для логгера используем общую фабрику
        _logger = logger;
        _dzenChannelId = (configuration["DzenOptions:SessionId"] ?? "").Trim();
        bool.TryParse(configuration["DzenOptions:IsGroomingMode"], out _isGroomingMode);
        bool.TryParse(configuration["DzenOptions:IsDebugDraftMode"], out _isDebugDraftMode);
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
        // ========================================================
        // ШАГ 1: ФОРМАТИРОВАНИЕ HTML-ТАБЛИЦ В ТЕКСТОВУЮ ПСЕВДОГРАФИКУ
        // ========================================================
        string textWithFormattedTables = contentHtml;
        try
        {
            var tableMatches = Regex.Matches(textWithFormattedTables, @"<table[^>]*>(.*?)<\/table>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match tableMatch in tableMatches)
            {
                var rows = Regex.Matches(tableMatch.Groups[1].Value, @"<tr[^>]*>(.*?)<\/tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (rows.Count <= 1) continue;

                var sbTable = new StringBuilder();
                sbTable.AppendLine("\n 📊 СРАВНИТЕЛЬНЫЕ ХАРАКТЕРИСТИКИ МОДЕЛЕЙ:");
                sbTable.AppendLine("───────────────────────────────────");

                var headers = Regex.Matches(rows[0].Groups[1].Value, @"<th[^>]*>(.*?)<\/th>|<td[^>]*>(.*?)<\/td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                                   .Cast<Match>()
                                   .Select(m => Regex.Replace(m.Value, @"<[^>]*>", "").Trim()).ToList();

                for (int i = 1; i < rows.Count; i++)
                {
                    var cells = Regex.Matches(rows[i].Groups[1].Value, @"<td[^>]*>(.*?)<\/td>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (cells.Count == 0) continue;

                    sbTable.AppendLine("🔹 " + Regex.Replace(cells[0].Groups[1].Value, @"<[^>]*>", "").Trim().ToUpper());
                    for (int j = 1; j < cells.Count && j < headers.Count; j++)
                    {
                        sbTable.AppendLine(" ▪ " + headers[j] + ": " + Regex.Replace(cells[j].Groups[1].Value, @"<[^>]*>", "").Trim());
                    }
                    sbTable.AppendLine();
                }
                sbTable.AppendLine("───────────────────────────────────");
                textWithFormattedTables = textWithFormattedTables.Replace(tableMatch.Value, sbTable.ToString());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DZEN] Ошибка конвертации HTML-таблицы: " + ex.Message);
        }

        try
        {
            // 2. НАСТРОЙКА ИЗОЛИРОВАННОГО ПРОФИЛЯ ДЛЯ ВХОДА
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

            // 3. ПРОВЕРКА АВТОРИЗАЦИИ НА ГЛАВНОЙ СТРАНИЦЕ
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
                    "Пожалуйста, авторизуйтесь под своим аккаунтом в окне Chrome.\n\nПосле успешного входа нажмите ОК в этом окне!",
                    "Авторизация в Дзен",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information
                );
                await Task.Delay(3000);
            }

            // 4. ПЕРЕХОД В ВАШУ ЛИЧНУЮ СТУДИЮ ПО ХАРДКОД-ССЫЛКЕ
            string studioUrl = "https://" + "dzen." + "ru/" + "profile/" + "editor/" + "id/" + _dzenChannelId + "/" + "publications";
            _driver.Navigate().GoToUrl(studioUrl);
            _logger.LogInformation("[DZEN] Открываю вашу личную Студию: " + studioUrl);

            var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));
            await Task.Delay(4000);

            // Нажимаем плюсик
            _logger.LogInformation("[DZEN] Нажимаю кнопку плюсика добавления публикации...");
            IWebElement addButton = wait.Until(d => d.FindElement(By.XPath("//button[@data-testid='add-publication-button']")));
            addButton.Click();
            await Task.Delay(1500);

            // Нажимаем "Написать статью"
            _logger.LogInformation("[DZEN] Нажимаю пункт меню 'Написать статью'...");
            IWebElement writeArticleOption = wait.Until(d => d.FindElement(By.XPath("//*[@aria-label='Написать статью']")));
            writeArticleOption.Click();

            _logger.LogInformation("[DZEN] Ожидаю загрузку бланка редактора черновика...");
            await Task.Delay(6000);

            // 🔥 Передаем ОТФОРМАТИРОВАННЫЙ текст "textWithFormattedTables" вместо сырого "contentHtml"
            return await CompletePublication(title, textWithFormattedTables, metadata, imageBytes);
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
        // ШАГ 2: ВВОД ОСНОВНОГО ТЕКСТА (ФИКС ОШИБКИ BMP ЧЕРЕЗ БУФЕР ОБМЕНА)
        // --------------------------------------------------------
        try
        {
            // Базовая очистка HTML-разметки исходной статьи
            string cleanText = Regex.Replace(contentHtml.Replace("<p>", "\n\n").Replace("<br>", "\n"), @"<[^>]*>", "");

            if (_isGroomingMode)
            {
                _logger.LogInformation("[DZEN] Включен режим прогрева канала. Удаляю CPA-ссылки...");
                cleanText = Regex.Replace(cleanText, @"https?://[^\s]+", "[информация доступна в источнике]");
            }
            else
            {
                _logger.LogInformation("[DZEN] Режим прогрева отключен. Формирую рекламный хвост со ссылкой на сайт...");

                // Генерируем маскированную CPA-ссылку на сайт (используем ваш хелпер)
                string domainId = "tech-info";
                string maskedCpaUrl = Application.Helpers.CpaLinkHelper.GenerateMaskedVkLink(title, domainId);

                // Собираем текстовый рекламный блок (без HTML-тегов, Дзен сам сделает URL кликабельным)
                var sbDzenTail = new StringBuilder();
                sbDzenTail.AppendLine("\n\n🚀 Читать полный обзор и сравнить актуальные цены на нашем сайте:");
                sbDzenTail.AppendLine(maskedCpaUrl);

                // Дописываем хвост к очищенному тексту статьи
                cleanText += sbDzenTail.ToString();
            }

            IWebElement bodyField = wait.Until(d => d.FindElement(By.XPath(
                "//div[@contenteditable='true' and @aria-describedby='placeholder-ZenDraftEditor'] " +
                "| //div[contains(@class, 'zenDraftEditor')]//div[@contenteditable='true']"
            )));

            bodyField.Click();
            await Task.Delay(500);

            // 🔥 ГЛАВНЫЙ ФИКС: Записываем текст со всеми эмодзи в буфер обмена в STA-потоке
            var staThread = new System.Threading.Thread(() =>
            {
                try
                {
                    System.Windows.Forms.Clipboard.SetText(cleanText);
                }
                catch (Exception clipboardEx)
                {
                    _logger.LogError("[DZEN] Ошибка записи текста статьи в буфер: " + clipboardEx.Message);
                }
            });
            staThread.SetApartmentState(System.Threading.ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            // Вставляем текст из буфера обмена в редактор Дзена нажатием Ctrl + V
            bodyField.SendKeys(Keys.Control + "v");
            await ClipboardHistoryHelper.DeleteLastItemAsync(_logger);
            _logger.LogInformation("[DZEN] Основной текст статьи (включая эмодзи и таблицы) успешно вставлен через Ctrl+V.");
            await Task.Delay(1000);
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
        if (_isDebugDraftMode)
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
