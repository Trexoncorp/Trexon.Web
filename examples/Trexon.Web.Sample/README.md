# Полный пример: сервис задач

[Руководство](../../docs/README.md) · [English](../../docs/en/README.md) · [Deutsch](../../docs/de/README.md)

Пример показывает совместную работу функций, а не независимые Hello World:
у каждого аккаунта свои задачи, данные переживают перезапуск, администрация требует
роли/политики, интеграционный endpoint принимает только API key.

## 1. Запуск одной командой

Из корня репозитория:

```sh
dotnet run --project examples/Trexon.Web.Sample
```

Документация: `http://localhost:5080/docs`, схема: `/openapi.json`, здоровье: `/health`.
Остановка — Ctrl+C. До открытия listener модуль пользователей создаёт schema accounts,
TaskModule обновляет schema tasks через Trexon.Data, затем при необходимости создаётся admin.
Фоновых jobs нет; HTTP-запросы обрабатываются параллельно стандартным ASP.NET Core.

По умолчанию JWT key случайный на процесс; API key не задан, admin не создаётся.
Это позволяет изучить регистрацию, JWT и CRUD сразу, без ввода конфигурации.

## 2. Настроить все способы доступа

Для POSIX shell перед запуском:

```sh
export TREXON_JWT_KEY="$(openssl rand -hex 32)"
export TREXON_API_KEY="$(openssl rand -hex 32)"
export TREXON_ADMIN_PASSWORD="$(openssl rand -hex 24)"
export TREXON_DATA_DIR="$PWD/.local-data"
dotnet run --project examples/Trexon.Web.Sample
```

Используйте те же environment variables в другом терминале, где выполняете запросы.
Секреты не выводятся приложением. Каталог `.local-data` добавлен в `.gitignore`.
При перезапуске с существующим admin переданный пароль должен совпасть: пример не
повышает аккаунт с совпавшим именем без проверки пароля и не меняет его пароль молча.

| Переменная | Default | Значение |
| --- | --- | --- |
| TREXON_URL | `http://127.0.0.1:5080` | Адрес Kestrel |
| TREXON_DATA_DIR | `data` рядом с sample DLL | accounts.db и tasks.db |
| TREXON_JWT_KEY | Случайный на процесс | Стабильный ключ для access JWT |
| TREXON_API_KEY | Пусто | Единственный демонстрационный API key |
| TREXON_ADMIN_PASSWORD | Пусто | Пароль создаваемого/проверяемого admin |
| TREXON_FRONTEND_ORIGIN | `http://localhost:5173` | Разрешённый CORS origin |

Настройки читает Trexon.Configuration из окружения. Наличие файла `.env` само по себе
не загружает его: настройте источник dotenv явно либо экспортируйте переменные.

## 3. Регистрация и JWT

В отдельном терминале:

```sh
BASE=http://localhost:5080
PASSWORD="$(openssl rand -hex 16)"
curl -i -X POST "$BASE/auth/register" -H 'Content-Type: application/json' \
  -d "{\"username\":\"alice\",\"password\":\"$PASSWORD\"}"
TOKEN=$(curl -fsS -X POST "$BASE/auth/login" -H 'Content-Type: application/json' \
  -d "{\"username\":\"alice\",\"password\":\"$PASSWORD\"}" \
  | python3 -c 'import sys,json; print(json.load(sys.stdin)["accessToken"])')
curl -i "$BASE/auth/me" -H "Authorization: Bearer $TOKEN"
```

Повторная регистрация username возвращает 409; имена сравниваются без учёта регистра.
Если alice уже есть после предыдущего запуска, войдите с исходным паролем либо используйте
новое имя. Значение roles из запроса не даёт дополнительных прав.

В Swagger выполните register/login. Скопируйте `accessToken`, нажмите **Authorize**,
вставьте его в поле Bearer без префикса. Закрытые операции станут доступны этому пользователю.

## 4. CRUD, binding, валидация и владелец

```sh
curl -i -X POST "$BASE/tasks" -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"title":"Explore Trexon.Web"}'
curl -i "$BASE/tasks?completed=false&take=10" -H "Authorization: Bearer $TOKEN"
```

POST возвращает 201, модель задачи, `Location` и заголовок собственного модуля
`X-Task-Module: enabled`. Используйте реальный ID из ответа вместо `1`:

```sh
curl -i "$BASE/tasks/1" -H "Authorization: Bearer $TOKEN"
curl -i -X PUT "$BASE/tasks/1" -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"title":"Read module guide","completed":true}'
curl -i -X DELETE "$BASE/tasks/1" -H "Authorization: Bearer $TOKEN"
```

Пустой title нарушает DataAnnotations, title из одних пробелов — пользовательский
`TaskTitleValidator`. `take=1000` нарушает параметр `[Range(1,100)]`. Все эти запросы дают
400 и `errors`. Запрос без auth даёт 401. Войдите вторым пользователем и запросите ID
alice: получите 404, поскольку проверяется `OwnerId`, а не только наличие токена.

## 5. Роли, политики и API keys

Обычный пользователь получает 403 на `/demo/admin` и `/demo/policy`.
Если сервер запущен с `TREXON_ADMIN_PASSWORD`, войдите как admin:

```sh
ADMIN_TOKEN=$(curl -fsS -X POST "$BASE/auth/login" -H 'Content-Type: application/json' \
  -d "{\"username\":\"admin\",\"password\":\"$TREXON_ADMIN_PASSWORD\"}" \
  | python3 -c 'import sys,json; print(json.load(sys.stdin)["accessToken"])')
curl -i "$BASE/demo/admin" -H "Authorization: Bearer $ADMIN_TOKEN"
curl -i "$BASE/demo/policy" -H "Authorization: Bearer $ADMIN_TOKEN"
curl -i "$BASE/demo/integration" -H "X-API-Key: $TREXON_API_KEY"
```

`/demo/admin` использует роль, `/demo/policy` — policy `manage-tasks`, а
`/demo/integration` требует именно ApiKey. Валидный JWT на последнем endpoint не заменяет
API key. В Swagger заполните секцию ApiKey значением настроенной переменной окружения.

## 6. Refresh и logout

Для изучения refresh сохраните весь JSON login, а не только accessToken. Передайте его
`refreshToken` в `POST /auth/refresh`. Ответ содержит новую пару; старый refresh использовать
нельзя. Повтор старого refresh даёт 401 и отзывает семейство, включая новый токен.

`POST /auth/logout` принимает `{"refreshToken":"..."}` и возвращает 204. После этого
refresh больше не работает, но access JWT продолжает действовать до expiry. Это
[явный контракт](../../docs/USERS.md), а не немедленный отзыв уже выданного access token.
Cookie logout выполняется отдельным endpoint.

## 7. Cookie-сессия с CSRF через HTTPS

Перезапустите с HTTPS, сохранив каталог данных и нужные настройки:

```sh
dotnet dev-certs https --trust
TREXON_URL=https://localhost:7080 dotnet run --project examples/Trexon.Web.Sample
```

Далее используется доверенный сертификат разработки:

```sh
BASE=https://localhost:7080
COOKIE_DIR=$(mktemp -d)
CSRF=$(curl -fsS -c "$COOKIE_DIR/cookies" "$BASE/auth/cookie/csrf" \
  | python3 -c 'import sys,json; print(json.load(sys.stdin)["requestToken"])')
curl -i -b "$COOKIE_DIR/cookies" -c "$COOKIE_DIR/cookies" \
  -X POST "$BASE/auth/cookie/login" -H "X-CSRF-Token: $CSRF" \
  -H 'Content-Type: application/json' \
  -d "{\"username\":\"alice\",\"password\":\"$PASSWORD\"}"
CSRF=$(curl -fsS -b "$COOKIE_DIR/cookies" -c "$COOKIE_DIR/cookies" "$BASE/auth/cookie/csrf" \
  | python3 -c 'import sys,json; print(json.load(sys.stdin)["requestToken"])')
curl -i -b "$COOKIE_DIR/cookies" "$BASE/auth/me"
curl -i -b "$COOKIE_DIR/cookies" -X POST "$BASE/tasks" \
  -H "X-CSRF-Token: $CSRF" -H 'Content-Type: application/json' -d '{"title":"Cookie task"}'
curl -i -b "$COOKIE_DIR/cookies" -X POST "$BASE/auth/cookie/logout" -H "X-CSRF-Token: $CSRF"
rm -r "$COOKIE_DIR"
```

После входа CSRF token запрашивается заново: он связан с аутентифицированной личностью.
Без него изменяющий запрос с cookie-сессией получает 400. Браузер Swagger отправляет
HTTP-only cookie автоматически после входа на том же origin, но автоматической подстановки
CSRF-заголовка в UI сейчас нет. Полный cookie-поток воспроизводится приведёнными командами
и покрыт HTTP-тестами.

## 8. Остальные возможности

| Запрос | Что демонстрирует |
| --- | --- |
| GET `/demo/public` | Объект → JSON, без auth |
| GET `/demo/headers`, `X-Client: desktop` | `[FromHeader]` |
| GET `/demo/cached` дважды | Одинаковый публичный ответ 30 секунд |
| GET `/demo/conflict` | Намеренная ошибка 409 |
| GET `/demo/failure` | 500 без внутренних подробностей, исключение в ILogger |
| GET `/health` | Общий статус и проверка SQLite tasks |
| Любой ответ | `X-Request-ID` |
| Более 100 запросов в минуту на client key | 429 + Retry-After |
| OPTIONS с настроенным Origin | CORS preflight |

Кеш не переиспользует авторизованные ответы. Лимитер и кеш локальны одному процессу.
HTTP-логи идут через Trexon.Logging; исключения — через стандартный ILogger хоста.

## 9. Где смотреть код

Код примера прокомментирован на русском. Начните с `Program.cs`, затем прочитайте
модуль и маршруты задач; WebSocket-обработчик и браузерный клиент можно изучать отдельно.
Комментарии объясняют этапы запуска, источники параметров, проверки доступа и отмену I/O.

- [Program.cs](Program.cs): состав приложения и подключение готовых модулей.
- [SampleSettings.cs](SampleSettings.cs): типизированные env-настройки.
- [TaskModule.cs](TaskModule.cs): свой модуль, async startup, Trexon.Data, свой validator, middleware и health check.
- [TaskRoutes.cs](TaskRoutes.cs): полный CRUD, параметрические ограничения, статусы и описания.
- [DiagnosticsRoutes.cs](DiagnosticsRoutes.cs): роли, policy, API key validator, кеш и ошибки.
- [ChatSocket.cs](ChatSocket.cs): жизненный цикл подключения, проверка сообщений и рассылка по комнатам.
- [Chat.html](Chat.html): HTML/CSS и JavaScript клиента с пояснениями cookie-входа и WebSocket-событий.
- [requests.http](requests.http): последовательный сценарий для HTTP-клиента IDE с ожидаемыми статусами.
- [Trexon.Web.Sample.csproj](Trexon.Web.Sample.csproj): зависимости и встраивание страницы чата в сборку.

Каждая операция ввода-вывода передаёт CancellationToken. SQLite-драйвер внутри остаётся
синхронным; ограничение описано в [главе о пользователях](../../docs/USERS.md).

## 10. Полная автоматическая проверка

```sh
dotnet build Trexon.Web.slnx --configuration Release -warnaserror
python3 scripts/smoke-sample.py
```

Скрипт использует случайные секреты, временный каталог и временный loopback-порт.
Проверяет Swagger, регистрацию, роли, policy, API key, CRUD и ownership, validation,
кеш, ошибки, refresh/reuse/logout. Сервер и данные удаляются после проверки.

## 11. WebSocket-чат: браузер и native-клиент

После обычного запуска откройте `http://localhost:5080/chat` в двух вкладках. Выберите
одну комнату и разные отображаемые имена, подключитесь и отправьте сообщение. Обе вкладки
получат событие. Подключение в другой комнате изолировано; история не сохраняется.
Публичные имена — подписи, а не подтверждённые личности. Элементы страницы вставляют
сообщения как текст, а не HTML.

- `/ws/chat/{room}` — публичный чат, вход ChatMessage, выход ChatEvent.
- `/ws/private/{room}` — тот же обработчик с `[Authorize]` и отдельным реестром комнат.
- `/chat` — встроенный браузерный клиент без сторонних JS-зависимостей.

Для защищённого режима запустите HTTPS-вариант из главы 7, откройте `/chat`, раскройте
cookie-вход и войдите существующим аккаунтом. Клиент сам получает CSRF-токен для login,
а WebSocket использует установленную HTTP-only cookie. Выберите режим «С авторизацией».
Имя отправителя сервер берёт из аутентифицированной identity, а не из поля сообщения.
Заголовок Origin проверяется перед upgrade. Не передавайте JWT в URL.

Native-клиенты могут подключиться к private endpoint с Authorization: Bearer или
X-API-Key. При отсутствии/неверных credentials upgrade отклоняется. После подключения
отправьте JSON `{"name":"Alice","text":"Hello"}`. Пустой текст получит validation error;
соединение остаётся открытым для исправленного сообщения.

[ChatSocket.cs](ChatSocket.cs) показывает OnConnectedAsync, группы, типизированную рассылку,
DI TimeProvider и повторное использование обработчика для public/private режимов.
[Chat.html](Chat.html) содержит браузерный клиент. В `/docs` автоматически появился раздел
WebSockets, а в `/openapi.json` — x-trexon-websockets и схемы сообщений.
[Полное руководство](../../docs/WEBSOCKETS.md).
