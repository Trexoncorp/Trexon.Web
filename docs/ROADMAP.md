# Границы версии 1.0.0-dev

[Оглавление](README.md)

## Реализовано

Явные классы маршрутов и HTTP/документационные атрибуты, async startup и module
initialization, Swagger UI и OpenAPI, MVC binding/DataAnnotations, свои async-валидаторы,
единые ошибки и request ID, local/external JWT, API keys, cookies с CSRF, роли/политики,
локальные аккаунты с SQLite и заменяемым store, refresh rotation/reuse detection/logout,
CORS, fixed-window rate limit, public output cache, health checks, request logs.
Полный пример соединяет Trexon.Configuration, Trexon.Logging и Trexon.Data.

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
модули uploads, WebSocket, streaming и client generation пока не оформлены.

## Возможные следующие этапы

Приоритет определяется реальными приложениями: улучшение UX Swagger cookie auth,
отдельные пакеты модулей, сетевое хранилище аккаунтов, версии схемы, benchmark с равными
условиями и проект-шаблон `dotnet new`. Это направления развития, не текущие функции.
