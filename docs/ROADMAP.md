# Границы версии 1.1.0

[Оглавление](README.md)

## Реализовано

Явные классы маршрутов и HTTP/документационные атрибуты, async startup и module
initialization, Swagger UI и OpenAPI, MVC binding/DataAnnotations, свои async-валидаторы,
единые ошибки и request ID, local/external JWT, API keys, cookies с CSRF, роли/политики,
локальные аккаунты с SQLite и заменяемым store, refresh rotation/reuse detection/logout,
CORS, fixed-window rate limit, public output cache, health checks, request logs,
типизированные WebSocket endpoints с комнатами и валидацией.
Полный пример соединяет Trexon.Configuration, Trexon.Logging и Trexon.Data.
Отдельный пакет [Trexon.Templates](../templates/README.md) создаёт стартовый API
командой `dotnet new trexon-api`, включая опции `--docker --tests`.
В 1.1 добавлены ICurrentUser, scoped startup, ранняя диагностика DI/policies,
message scopes WebSocket и отдельный тестовый клиент Trexon.Web.Testing.

## Чего версия не обещает

- Native AOT, trimming, генерацию маршрутов без reflection.
- Измеренное преимущество RPS/latency/allocations над Minimal APIs.
- OAuth/OIDC identity server, MFA, email verification, password reset, account lockout.
- Немедленный отзыв access JWT/cookie при logout или смене ролей.
- Готовые сетевые IUserStore, распределённые лимиты и кеш из одного вызова.
- Автоматические миграции версии account schema и фоновую очистку refresh-записей.
- Настоящее async SQLite I/O: ограничение самого Microsoft.Data.Sqlite.
- Авторизацию всех endpoints по умолчанию: `[Authorize]` или fallback policy задаёт приложение.
- Автоматическую CSRF-подстановку Swagger UI для cookie-запросов.
- Полную автоматическую JSON Schema для произвольных business validators.
- Совпадение всех возможностей кастомных policies с описанием OpenAPI.
- Планировщик, очередь задач, фоновые workers, hot loading модулей.

Файловые ответы и multipart доступны через стандартный MVC API, но специализированные
модули uploads, binary streaming и client generation пока не оформлены.
WebSocket API описан в [отдельной главе](WEBSOCKETS.md); он не реализует SignalR/backplane.

## Возможные следующие этапы

Приоритет определяется реальными приложениями: улучшение UX Swagger cookie auth,
отдельные пакеты модулей, сетевое хранилище аккаунтов, версии схемы и benchmark с равными
условиями. Это направления развития, не текущие функции.
