# Разработка и проверки

[Оглавление](README.md)

## Структура

- `Trexon.Web/` — библиотека net8.0.
- `examples/Trexon.Web.Calculator/` — маленький API в одном файле, используется в HTTP-тестах.
- `examples/Trexon.Web.Sample/` — полный сервис задач и интеграции Trexon.
- `tests/Trexon.Web.Tests/` — HTTP и storage/lifecycle-тесты, net8.0 и net10.0.
- `docs/` — русское руководство; `docs/en`, `docs/de` — самостоятельные краткие руководства.
- `scripts/check-docs.py` — проверка ссылок и компиляция завершённых C#-примеров.
- `scripts/smoke-sample.py` — полный пример через реальный Kestrel на временном порту.

## Команды

```sh
dotnet restore Trexon.Web.slnx
dotnet build Trexon.Web.slnx --configuration Release --no-restore -warnaserror
dotnet test --solution Trexon.Web.slnx --configuration Release --no-build --no-restore
python3 scripts/check-docs.py
python3 scripts/smoke-sample.py
dotnet pack Trexon.Web/Trexon.Web.csproj --configuration Release --no-build --output artifacts
dotnet list Trexon.Web.slnx package --vulnerable --include-transitive
```

SDK 10 использует Microsoft.Testing.Platform, поэтому нужен `--solution`, как в соседних
проектах. Тестовые проекты используют TestHost соответствующей major-версии runtime;
смешивание TestHost 8 с runtime 10 нарушает контракт PipeWriter и не является проверкой
совместимости библиотеки.

Интеграционные тесты проверяют binding, рекурсивную DataAnnotations-валидацию, свои правила,
формат ошибок, Swagger, явное подключение routes, конфликты, модули, JWT issuer/audience/
expiry/signature, roles/policies, API keys, пароли, refresh reuse/logout, cookie CSRF,
rate limit, caching, CORS, health, request logging, async startup/cancellation/disposal, запуск с портом через реальный Kestrel,
операции/валидацию/OpenAPI одностраничного примера калькулятора,
сохранность SQLite и конкурентную ротацию. WebSocket-тесты покрывают текст/JSON,
фрагменты, UTF-8, nested/custom validation, комнаты, параллельные рассылки, lifecycle/disposal,
JWT/roles/cookies, Origin, лимиты и shutdown. Есть проверка native ClientWebSocket через реальный Kestrel.

Большинство HTTP-тестов запускают TestServer без внешнего порта; отдельная WebSocket-проверка
поднимает Kestrel на временном loopback-порту. Для cookie-тестов используется HTTPS
scheme в TestServer; они не устанавливают доверенные сертификаты в ОС. Smoke-тест
использует реальный HTTP listener, временную БД и случайные секреты, затем останавливает
процесс и удаляет данные. Cookie-поток покрывается TestServer.

## Проверяемая документация

Завершённые snippets помечаются `<!-- verify: unique-id -->` перед C# fenced block.
Скрипт проверяет локальные Markdown-ссылки и компилирует каждый уникальный snippet
временным web-проектом с ProjectReference на текущую библиотеку. Он не запускает
RunAsync и не выполняет команды публикации. Повтор одного и того же ID на другом языке
разрешён только с идентичным C#-кодом.

Неполные фрагменты явно названы фрагментами и не помечаются verify. HTTP-сценарии полного
примера дополнительно исполняются smoke-тестом. Добавляя возможность, обновляйте контракт,
пример и соответствующую проверку, а не только список возможностей.

## CI и пакеты

GitHub Actions строит solution с предупреждениями как ошибками, запускает обе версии
тестов, проверяет документацию и полный пример, создаёт nupkg/snupkg. Публикация в NuGet
и создание стабильного релиза автоматически не включены. [Подготовка пакета](PUBLISHING.md).
