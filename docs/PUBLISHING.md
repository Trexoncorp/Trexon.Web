# Публикация GitHub и NuGet

[Оглавление](README.md)

Первый стабильный релиз — `1.0.0`; текущий релиз — `1.1.0`. Версия NuGet-пакета берётся из тега
`vMAJOR.MINOR.PATCH[-PRERELEASE]` и передаётся в build/pack через `Version`. Опубликованные версии и теги не перемещаются.
Контракт и ограничения выпуска описаны в [changelog](../CHANGELOG.md) и [границах версии](ROADMAP.md).

## Проверки перед тегом

CI текущего `main` должен пройти на Linux, Windows и macOS: Release-сборка с
предупреждениями как ошибками, тесты на .NET 8/10, компиляция документации, запуск
полного примера и установка nupkg в независимое приложение с чистым NuGet-кешем.

```sh
dotnet restore Trexon.Web.slnx
dotnet build Trexon.Web.slnx --configuration Release --no-restore -warnaserror
dotnet test --solution Trexon.Web.slnx --configuration Release --no-build --no-restore
python3 scripts/check-docs.py
python3 scripts/smoke-sample.py
dotnet pack Trexon.Web/Trexon.Web.csproj --configuration Release --no-build --output artifacts
python3 scripts/smoke-package.py --source artifacts --version 1.1.0
```

`scripts/smoke-package.py` создаёт новый web-проект без ProjectReference, восстанавливает
пакет и зависимости в пустой каталог, проверяет HTTP, Swagger, ресурсы WebSocket-консоли
и автоматический `Log.Info`. Базы/процессы проверок удаляются после завершения.

## GitHub Release

После обновления версии, README и CHANGELOG отправьте `main`, дождитесь CI, затем:

```sh
bash scripts/release-version.sh v1.1.0
git tag -a v1.1.0 -m "Trexon.Web 1.1.0"
git push origin v1.1.0
```

`.github/workflows/release.yml` повторяет полную матрицу CI, проверяет формат тега
и принадлежность коммита `origin/main`. Затем собирает и проверяет пакет,
публикует GitHub Release с nupkg, snupkg, описанием и `SHA256SUMS`.
Пакет содержит DLL net8.0, XML-документацию, README на трёх языках, changelog,
MIT license и зависимости. Примеры и тесты в пакет не входят.

## NuGet.org

После успешной упаковки workflow выполняет `NuGet/login` через OIDC и публикует
пакет и символы в NuGet. Нужна policy для `Trexoncorp/Trexon.Web`, workflow
`release.yml`, environment `nuget`, package scope `Trexon.Web`.
Постоянный API key и ручная загрузка файла не требуются.

После окончания проверки и индексации NuGet проверьте установку именно из публичного feed:

```sh
python3 scripts/smoke-package.py --version 1.1.0
```

Ожидаемый итог: пакет `Trexon.Web/1.1.0` восстанавливается в чистый кеш, приложение
запускается и HTTP/Swagger/WebSocket console/Log.Info проходят smoke-проверки.
Создание тега, загрузка файла и видимость страницы по отдельности ещё не подтверждают
доступность версии в публичном restore feed.

## Совместимость

Шаблон `dotnet new trexon-api` выпускается отдельным пакетом `Trexon.Templates`
со своей версией. CI сохраняет его nupkg и проверяет создание приложения;
`release.yml` публикует Trexon.Web, а `template-release.yml` — Trexon.Templates. Инструкции по сборке,
локальной установке и распространению — в [руководстве шаблона](../templates/README.md).

NuGet-пакет предназначен для ASP.NET Core-приложений на .NET 8 и новее; в CI
проверяются .NET 8 и .NET 10. Для изменений публичного API используется SemVer:
совместимые исправления — patch, новые совместимые возможности — minor,
несовместимые изменения — major. Для разработки в этом репозитории используется SDK 10.

## Автоматическая публикация через Trusted Publishing

Релизный workflow публикует проверенный nupkg и символы в NuGet через OIDC.
Постоянный API key не нужен. Логин — repository variable `NUGET_USER` или
`anton_aleynichenko` по умолчанию. Публикация использует environment `nuget`.

В NuGet policy укажите owner репозитория `Trexoncorp`, имя этого репозитория,
точное имя workflow и environment `nuget`; scope ограничьте соответствующим
Package ID и публикацией новых версий существующего пакета.

Для проверки настройки без загрузки пакета запустите workflow вручную на `main`:
выполнится только обмен OIDC-токена. Публикация выполняется исключительно по тегу.
Опубликованные теги не перемещайте; перед новым тегом обновите версию и документацию.

Для Trexon.Web используются `release.yml` и теги `vVERSION`.
Для Trexon.Templates — отдельная policy с `template-release.yml`, scope
`Trexon.Templates` и тегами `templates-vVERSION`. Версии шаблона и Web независимы;
имя nupkg шаблона вычисляется из его csproj.

Версии в csproj служат локальными значениями по умолчанию. Для релиза достаточно
нового SemVer-тега на коммите из main: версия пакета берётся из тега для build, pack
и smoke-проверок. Для шаблона используется префикс `templates-v`.

## Пакет тестирования 1.1

Релиз `v1.1.0` также содержит `Trexon.Web.Testing.1.1.0.nupkg` и snupkg. Он собирается
и проверяется на обеих целевых платформах отдельно от production-пакета.
Workflow публикует оба nupkg и оба snupkg через Trusted Publishing. NuGet-policy
для release.yml должна разрешать Trexon.Web и Trexon.Web.Testing. Для создания
ещё не существующего Package ID нужны scope публикации новых пакетов и подходящий
pattern; для следующих выпусков достаточно публикации новых версий этих двух пакетов.
Ручная первая загрузка возможна, но не является обязательным ограничением NuGet.
После индексации проверьте `python3 scripts/smoke-testing.py --version 1.1.0` без --source.
Шаблон `templates-v1.1.0` выпускайте после доступности Web и Testing в публичном feed.
