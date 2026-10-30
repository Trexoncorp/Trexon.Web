# Руководство Trexon.Web

[Главная](../README.ru.md) · [English](en/README.md) · [Deutsch](de/README.md)

Trexon.Web собирает повторяющуюся настройку небольшого HTTP-сервиса вокруг обычных
классов и атрибутов. Прочитайте главы по порядку для нового проекта либо откройте
нужный сценарий. Все описанные возможности относятся к версии `1.0.1`.

| Глава | Что получите |
| --- | --- |
| [1. Первый сервис](GETTING_STARTED.md) | Установка, приложение целиком, `/docs`, первый запрос |
| [Шаблон trexon-api](../templates/README.md) | Установка шаблона и генерация готового CRUD API |
| [2. Маршруты и ответы](ROUTING.md) | Префиксы, параметры, DI, async, статусы, Swagger-атрибуты |
| [3. Валидация](VALIDATION.md) | DataAnnotations, вложенные модели и свои async-валидаторы |
| [4. Ошибки](ERRORS.md) | Problem Details, исключения, request ID, замена обработчика |
| [5. Аутентификация и доступ](SECURITY.md) | JWT, внешний провайдер, API keys, cookies, роли, политики |
| [6. Пользователи](USERS.md) | Регистрация, пароли, refresh-ротация, выход, SQLite, своё хранилище |
| [7. Модули](MODULES.md) | Готовые модули, собственный модуль, async-инициализация |
| [WebSocket](WEBSOCKETS.md) | Типизированные сообщения, комнаты, auth, lifecycle и готовый чат |
| [8. Работающий пример](../examples/Trexon.Web.Sample/README.md) | Все функции в сервисе задач и команды проверки |
| [9. Minimal APIs](MINIMAL_APIS.md) | Сопоставление подходов и постепенное подключение |
| [10. Эксплуатация](OPERATIONS.md) | Настройки, HTTPS, прокси, состояние и несколько экземпляров |
| [11. Архитектура](ARCHITECTURE.md) | MVC/Kestrel, порядок middleware и async-контракт |
| [12. Справочник](API.md) | Основные методы, атрибуты, интерфейсы и настройки |
| [13. Разработка](DEVELOPMENT.md) | Тесты, проверка примеров, CI |
| [14. Упаковка](PUBLISHING.md) | NuGet, GitHub Release и проверка установки |
| [15. Границы](ROADMAP.md) | Что реализовано и что ещё не обещается |

Начать с практики: `dotnet run --project examples/Trexon.Web.Sample`, затем открыть
`http://localhost:5080/docs`. Полная проверка: `python3 scripts/smoke-sample.py` после Release-сборки.

- [Разработка в 1.1: тестовый клиент, текущий пользователь, scoped startup, диагностика и Docker](DEVELOPER_EXPERIENCE.md).
