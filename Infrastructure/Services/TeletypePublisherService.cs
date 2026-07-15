using AiSiteFiller.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System.Text;
using System.Text.RegularExpressions;
using Keys = OpenQA.Selenium.Keys;

namespace AiSiteFiller.Infrastructure.Services;

public class TeletypePublisherService : IPublisherService, IDisposable
{
    private readonly string _apiToken;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TeletypePublisherService> _logger;
    private IWebDriver? _driver;
    public string PlatformName => "TELETYPE";

    public TeletypePublisherService(IConfiguration configuration, ILogger<TeletypePublisherService> logger)
    {
        _configuration = configuration;
        _logger = (ILogger<TeletypePublisherService>)logger;
        // Вычитываем токен авторизации из конфига
        _apiToken = (configuration["TeletypeOptions:ApiToken"] ?? "").Trim();
    }
    public bool IsCookieExpiringSoon(IConfiguration configuration)
    {
        string dateStr = configuration["TeletypeOptions:ExpirationDate"] ?? "";
        if (DateTime.TryParse(dateStr, out DateTime expirationDate))
        {
            if ((expirationDate - DateTime.Now).TotalDays <= 7)
            {
                _logger.LogWarning("[TELETYPE] ВНИМАНИЕ: Срок действия куки истекает менее чем через неделю ({0})!", dateStr);

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
            // 1. ИНИЦИАЛИЗАЦИЯ ЧИСТОГО БРАУЗЕРА CHROME
            var options = new ChromeOptions();
            options.AddArgument("--start-maximized");
            options.AddArgument("--no-sandbox");
            options.AddArgument("--disable-dev-shm-usage");
            options.AddArgument("--user-agent=" + "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            _driver = new ChromeDriver(options);
            _logger.LogInformation("[TELETYPE] Чистый экземпляр Chrome успешно запущен.");

            string baseTeletypeUrl = "https://" + "teletype." + "in";
            _driver.Navigate().GoToUrl(baseTeletypeUrl);
            await Task.Delay(3000);

            // ========================================================
            // ШАГ 2: ПОЛУАВТОМАТИЧЕСКИЙ СЦЕНАРИЙ (Если в конфиге пусто)
            // ========================================================
            if (string.IsNullOrEmpty(_apiToken))
            {
                _logger.LogWarning("[TELETYPE] ⚠️ В конфигурации отсутствует ApiToken. Ожидаю ручной вход...");

                System.Windows.Forms.MessageBox.Show(
                    "В конфигурационном файле appsettings.json не найден токен Телетайпа.\n\n" +
                    "Пожалуйста, прямо сейчас в открывшемся окне браузера выполните вход (через Google или Telegram).\n\n" +
                    "После того как вы успешно авторизуетесь и попадете в личный кабинет, нажмите ОК в этом окне.",
                    "Требуется первая авторизация Teletype",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information
                );

                _logger.LogInformation("[TELETYPE] Модалка закрыта. Извлекаю куку сессии из браузера...");

                // Ищем куку авторизации. В Телетайпе она называется "token" или "auth_token"
                var extractedCookie = _driver.Manage().Cookies.GetCookieNamed("token")
                                   ?? _driver.Manage().Cookies.GetCookieNamed("auth_token");

                if (extractedCookie != null && !string.IsNullOrEmpty(extractedCookie.Value))
                {
                    string newTokenValue = extractedCookie.Value;

                    // Железобетонный фикс STA-потока для буфера обмена Windows
                    var staThread = new System.Threading.Thread(() =>
                    {
                        try { System.Windows.Forms.Clipboard.SetText(newTokenValue); } catch { }
                    });
                    staThread.SetApartmentState(System.Threading.ApartmentState.STA);
                    staThread.Start();
                    staThread.Join();

                    _logger.LogInformation("✅ [TELETYPE] Свежий токен авторизации успешно скопирован в буфер обмена!");

                    System.Windows.Forms.MessageBox.Show(
                        "Токен авторизации Телетайпа скопирован в буфер обмена!\n\n" +
                        "Сейчас приложение закроется. Откройте ваш файл appsettings.json и вставьте значение в поле 'ApiToken'.\n\n" +
                        "После этого перезапустите конвейер задач.",
                        "Токен скопирован",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Warning
                    );
                }
                else
                {
                    _logger.LogError("[TELETYPE] Ошибка: Не удалось найти куку авторизации. Вы точно вошли в аккаунт?");
                }

                CloseBrowser();
                return false;
            }

            // ========================================================
            // ШАГ 3: АВТОМАТИЧЕСКИЙ ВХОД (Если токен уже сохранен в конфиге)
            // ========================================================
            _logger.LogInformation("[TELETYPE] Токен найден в конфигурации. Внедряю куку сессии...");

            // Внедряем куку авторизации обратно в браузер
            var authCookie = new Cookie("token", _apiToken, "." + "teletype." + "in", "/", DateTime.Now.AddYears(1));
            _driver.Manage().Cookies.AddCookie(authCookie);

            _driver.Navigate().Refresh();
            await Task.Delay(4000);

            // ========================================================
            // ШАГ 4: РОУТИНГ НА БЛАНК НОВОЙ СТАТЬИ
            // ========================================================
            // Используем имя вашего коммерческого блога "@techinfo" из прошлого скриншота
            string editorUrl = "https://" + "teletype." + "in/" + "@techinfo" + "/editor";
            _driver.Navigate().GoToUrl(editorUrl);
            _logger.LogInformation("[TELETYPE] Перехожу по адресу редактора: " + editorUrl);

            await Task.Delay(5000); // Ожидаем полную загрузку бланка статьи

            return await CompletePublication(title, contentHtml, metadata, imageBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError("[TELETYPE] Критическая ошибка конвейера запуска: " + ex.Message);
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

        // Вычитываем логические флаги управления из конфига
        if (!bool.TryParse(_configuration["TeletypeOptions:IsGroomingMode"], out bool IsGroomingMode))
        {
            IsGroomingMode = true;
        }

        if (!bool.TryParse(_configuration["TeletypeOptions:IsDebugDraftMode"], out bool IsDebugDraftMode))
        {
            IsDebugDraftMode = true;
        }

        var wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(12));
        _logger.LogInformation("[TELETYPE] Бланк редактора открыт. Начинаю заполнение полей...");

        // --------------------------------------------------------
        // ШАГ 1: ЗАПОЛНЕНИЕ ЗАГОЛОВКА СТАТЬИ
        // --------------------------------------------------------
        try
        {
            IWebElement titleField = wait.Until(d => d.FindElement(By.XPath(
                "//h1[@contenteditable='true'] " +
                "| //*[contains(@class, 'title')]//h1 " +
                "| //*[@data-placeholder='Заголовок']"
            )));

            titleField.Click();
            await Task.Delay(500);
            titleField.SendKeys(title);
            _logger.LogInformation("[TELETYPE] Заголовок успешно заполнен.");

            titleField.SendKeys(Keys.Tab);
            await Task.Delay(1000);
        }
        catch (Exception ex)
        {
            _logger.LogError("[TELETYPE] ❌ Критическая ошибка: Не удалось заполнить заголовок: " + ex.Message);
            return false;
        }

        // --------------------------------------------------------
        // ШАГ 2-3: ОБЪЕДИНЕННЫЙ ИМПОРТ ТЕКСТА, ТАБЛИЦ И ОБЛОЖКИ (BASE64 ФИКС)
        // --------------------------------------------------------
        try
        {
            _logger.LogInformation("[TELETYPE] Подготавливаю HTML-контент (текст + таблицы + обложка)...");

            var sbFinalHtml = new StringBuilder();

            // 🔥 МОЩНЫЙ ФИКС ОБЛОЖКИ: Если есть байты картинки, переводим их в Base64 
            // и вставляем строго по структуре верстки Телетайпа в самое начало статьи!
            if (imageBytes?.Length > 0)
            {
                string base64Image = Convert.ToBase64String(imageBytes);

                sbFinalHtml.Append("<div class=\"w-figure_wrap\">");
                sbFinalHtml.Append("<img class=\"w-figure_image\" src=\"data:image/jpeg;base64,");
                sbFinalHtml.Append(base64Image);
                sbFinalHtml.Append("\" width=\"512\">");
                sbFinalHtml.Append("</div><br>");

                _logger.LogInformation("[TELETYPE] Картинка-обложка успешно упакована в Base64-тег.");
            }

            // Добавляем основное тело статьи с нативными HTML-таблицами характеристик
            // Жестко страхуем переменную от null с помощью оператора ??
            string textBody = (contentHtml ?? "").Trim();

            if (IsGroomingMode && !string.IsNullOrEmpty(textBody))
            {
                _logger.LogInformation("[TELETYPE] Включен режим прогрева канала. Маскирую CPA-ссылки...");
                try
                {
                    string replacedText = Regex.Replace(textBody, @"https?://[^\s]+", "[ссылка доступна в источнике]");
                    // Если регулярка отработала корректно и не вернула null — присваиваем значение
                    if (!string.IsNullOrEmpty(replacedText))
                    {
                        textBody = replacedText;
                    }
                }
                catch (Exception regEx)
                {
                    _logger.LogWarning("[TELETYPE] Ошибка работы Regex при маскировании ссылок: " + regEx.Message);
                    // В случае сбоя регулярки возвращаем оригинальный текст, чтобы не ломать поток
                    textBody = (contentHtml ?? "").Trim();
                }
            }

            // Теперь Append никогда не выкинет NullReferenceException!
            sbFinalHtml.Append(textBody);

            // Дописываем финальную рекламную плашку
            string maskedCpaUrl = Application.Helpers.CpaLinkHelper.GenerateMaskedVkLink(title, "tech-info");
            sbFinalHtml.Append("<br><br><p>🚀 <b>Подробные цены и наличие моделей смотрите на сайте:</b> <a href=\"");
            sbFinalHtml.Append(maskedCpaUrl);
            sbFinalHtml.Append("\">");
            sbFinalHtml.Append(maskedCpaUrl);
            sbFinalHtml.Append("</a></p>");

            string totalArticleHtml = sbFinalHtml.ToString();

            // Записываем весь готовый пирог (Текст + Таблица + Фото) в буфер обмена в STA-потоке Windows
            var staThread = new System.Threading.Thread(() =>
            {
                try { System.Windows.Forms.Clipboard.SetText(totalArticleHtml); } catch { }
            });
            staThread.SetApartmentState(System.Threading.ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            // Находим контентное поле Телетайпа по вашему точному классу editorPage__text
            IWebElement bodyField = wait.Until(d => d.FindElement(By.XPath(
                "//div[contains(@class, 'editorPage__text')] " +
                "| //div[@contenteditable='true' and contains(@data-placeholder, 'Начните свой рассказ')]"
            )));

            bodyField.Click();
            await Task.Delay(500);

            // Вставляем всё за один миг через Ctrl + V. Телетайп сам распарсит Base64 и загрузит на свои сервера!
            bodyField.SendKeys(Keys.Control + "v");
            _logger.LogInformation("[TELETYPE] Весь лонгрид (Обложка + Текст + Таблицы) успешно импортирован через буфер!");
            await Task.Delay(3000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[TELETYPE] ⚠️ Предупреждение: Не удалось автоматически вставить тело статьи: " + ex.Message);
        }

        // --------------------------------------------------------
        // ШАГ 4: НАЖАТИЕ ВЕРХНЕЙ КНОПКИ ПУБЛИКАЦИИ (ПО ВЕРСТКЕ С ИКОНКОЙ SEND)
        // --------------------------------------------------------
        try
        {
            _logger.LogInformation("[TELETYPE] Вызываю финальное меню настроек через класс 'publishButton'...");

            // Локатор бьет точно в предоставленный вами класс publishButton и иконку send
            IWebElement publishTopButton = wait.Until(d => d.FindElement(By.XPath(
                "//button[contains(@class, 'publishButton')]" +
                "| //button[@title='Опубликовать']" +
                "| //svg[@data-icon='send']/ancestor::button"
            )));

            publishTopButton.Click();
            _logger.LogInformation("[TELETYPE] Финальное меню настроек публикации успешно открыто.");
            await Task.Delay(2500); // Даем шторке пару секунд на анимацию появления
        }
        catch (Exception ex)
        {
            _logger.LogError("[TELETYPE] ❌ КРИТИЧЕСКИЙ СБОЙ: Не удалось вызвать меню публикации: " + ex.Message);
            return false;
        }

        // --------------------------------------------------------
        // ШАГ 5: ПЕРЕКЛЮЧЕНИЕ ПРИВАТНОСТИ ПОСТА (ПО ВЕРСТКЕ ШТОРКИ)
        // --------------------------------------------------------
        if (IsDebugDraftMode)
        {
            try
            {
                _logger.LogInformation("[TELETYPE] Режим отладки активен. Ищу радиокнопку 'Черновик' по вашей верстке...");

                // Находим строго input с value="draft" по коду из вашего файла 1.html
                IWebElement draftRadio = _driver.FindElement(By.XPath(
                    "//input[@name='visibility' and @value='draft']" +
                    "| //div[contains(@class, 'visibility')]//input[@value='draft']"
                ));

                // В Vue.js/Телетайпе кликать нужно по родительскому контейнеру label или элементу-обертке,
                // так как сам input может быть скрыт стилями opacity
                IWebElement radioLabel = draftRadio.FindElement(By.XPath("./ancestor::label"));
                radioLabel.Click();

                _logger.LogInformation("[DZEN] Радиокнопка 'Черновик' успешно активирована в шторке.");
                await Task.Delay(1000);
            }
            catch (Exception cbEx)
            {
                _logger.LogWarning("[TELETYPE] ⚠️ Предупреждение: Не удалось переключить приватность на 'Черновик': " + cbEx.Message);
            }
        }

        // --------------------------------------------------------
        // ШАГ 6: НАЖАТИЕ ФИНАЛЬНОЙ КНОПКИ ПОДТВЕРЖДЕНИЯ (ПО ВЕРСТКЕ)
        // --------------------------------------------------------
        try
        {
            _logger.LogInformation("[TELETYPE] Нажимаю финальную кнопку подтверждения публикации в шторке...");

            // Локатор бьет точно в класс контейнера кнопки отправки из вашего файла 1.html
            IWebElement finalConfirmButton = wait.Until(d => d.FindElement(By.XPath(
                "//div[contains(@class, 'editorPublisher__submit')] " +
                "| //svg[@data-icon='submit']/ancestor::div[contains(@class, 'submit')] " +
                "| //div[contains(@class, 'editorPublisher__menu')]//button"
            )));

            finalConfirmButton.Click();

            if (IsDebugDraftMode)
            {
                _logger.LogInformation("✅ [TELETYPE] Статья успешно сохранена в ваши личные черновики блога TechInfo!");
            }
            else
            {
                _logger.LogInformation("✅ [TELETYPE] Статья успешно улетела в живую ленту и открыта для поиска Яндекса!");
            }

            await Task.Delay(4000);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("[TELETYPE] ❌ КРИТИЧЕСКИЙ СБОЙ: Не удалось подтвердить финальное сохранение: " + ex.Message);
            return false; // Хардкодный возврат false при критической ошибке финала
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
                _logger.LogInformation("[TELETYPE] Сессия браузера Chrome успешно закрыта.");
            }
            catch { }
        }
    }

    public void Dispose()
    {
        CloseBrowser();
    }
}
