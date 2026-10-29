using Trexon.Configuration;

namespace Trexon.Web.Sample;

// Типизированные настройки: [Env] задаёт имя переменной окружения,
// значение свойства используется, если соответствующая переменная не задана.
public sealed class SampleSettings
{
    // Адрес прослушивания Kestrel. Для cookie-входа задайте HTTPS-адрес с сертификатом.
    [Env("TREXON_URL")]
    public string Url { get; init; } = "http://127.0.0.1:5080";

    // По умолчанию данные лежат рядом со сборкой, а не в текущем каталоге терминала.
    [Env("TREXON_DATA_DIR")]
    public string DataDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "data");

    // [Secret] помечает значение для сокрытия в диагностике Trexon.Configuration.
    // Это метаданные, а не шифрование. Пустой ключ заменяется случайным в Program.cs.
    [Env("TREXON_JWT_KEY"), Secret]
    public string JwtKey { get; init; } = "";

    // Пустое значение отключает успешную проверку демонстрационного API key.
    [Env("TREXON_API_KEY"), Secret]
    public string ApiKey { get; init; } = "";

    // Если пароль не задан, AdminSeed не создаёт администратора.
    [Env("TREXON_ADMIN_PASSWORD"), Secret]
    public string AdminPassword { get; init; } = "";

    // Дополнительный origin фронтенда для HTTP CORS и проверки WebSocket Origin.
    [Env("TREXON_FRONTEND_ORIGIN")]
    public string FrontendOrigin { get; init; } = "http://localhost:5173";
}
