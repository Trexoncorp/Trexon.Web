# Публикация GitHub и NuGet

[Оглавление](README.md)

Первый стабильный релиз — `1.0.0`; текущий патч — `1.0.1`. Версия в csproj должна совпадать с тегом
`vMAJOR.MINOR.PATCH[-PRERELEASE]`. Опубликованные версии и теги не перемещаются.
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
python3 scripts/smoke-package.py --source artifacts --version 1.0.1
```

`scripts/smoke-package.py` создаёт новый web-проект без ProjectReference, восстанавливает
пакет и зависимости в пустой каталог, проверяет HTTP, Swagger, ресурсы WebSocket-консоли
и автоматический `Log.Info`. Базы/процессы проверок удаляются после завершения.

## GitHub Release

После обновления версии, README и CHANGELOG отправьте `main`, дождитесь CI, затем:

```sh
bash scripts/release-version.sh v1.0.1
git tag -a v1.0.1 -m "Trexon.Web 1.0.1"
git push origin v1.0.1
```

`.github/workflows/release.yml` повторяет полную матрицу CI, проверяет совпадение
версии и принадлежность коммита `origin/main`. Затем собирает и проверяет пакет,
публикует GitHub Release с nupkg, snupkg, описанием и `SHA256SUMS`.
Пакет содержит DLL net8.0, XML-документацию, README на трёх языках, changelog,
MIT license и зависимости. Примеры и тесты в пакет не входят.

## NuGet.org

Пакет публикуется через [Upload](https://www.nuget.org/packages/manage/upload) из
аккаунта владельца. Загрузите **nupkg из успешного GitHub Release**, проверьте
Package ID `Trexon.Web`, версию и зависимости, затем подтвердите публикацию.
Отдельно загрузите snupkg из того же релиза. Этот способ не требует API key или
создания дополнительных прав доступа для workflow.

После окончания проверки и индексации NuGet проверьте установку именно из публичного feed:

```sh
python3 scripts/smoke-package.py --version 1.0.1
```

Ожидаемый итог: пакет `Trexon.Web/1.0.1` восстанавливается в чистый кеш, приложение
запускается и HTTP/Swagger/WebSocket console/Log.Info проходят smoke-проверки.
Создание тега, загрузка файла и видимость страницы по отдельности ещё не подтверждают
доступность версии в публичном restore feed.

## Совместимость

Шаблон `dotnet new trexon-api` выпускается отдельным пакетом `Trexon.Templates`
со своей версией. CI сохраняет его nupkg и проверяет создание приложения;
этот release workflow публикует только Trexon.Web. Инструкции по сборке,
локальной установке и распространению — в [руководстве шаблона](../templates/README.md).

NuGet-пакет предназначен для ASP.NET Core-приложений на .NET 8 и новее; в CI
проверяются .NET 8 и .NET 10. Для изменений публичного API используется SemVer:
совместимые исправления — patch, новые совместимые возможности — minor,
несовместимые изменения — major. Для разработки в этом репозитории используется SDK 10.
