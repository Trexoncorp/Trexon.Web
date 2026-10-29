# Сборка пакета и первый релиз

[Оглавление](README.md)

Текущий `1.0.0-dev` не опубликован. Наличие PackageId и Git remote не означает, что
пакет уже доступен в NuGet. Не используйте в документации установки несуществующую версию.

## Локальный пакет

Из корня репозитория:

```sh
dotnet build Trexon.Web.slnx --configuration Release -warnaserror
dotnet pack Trexon.Web/Trexon.Web.csproj --configuration Release --no-build --output artifacts
```

Создаются `.nupkg` и `.snupkg`; внутри — DLL net8.0, XML-комментарии, README на трёх
языках, MIT license, ссылка на репозиторий и зависимости. Сборки примера и тестов не
публикуются в пакет. Для использования локальной сборки:

```sh
dotnet add Hello/Hello.csproj package Trexon.Web --version 1.0.0-dev --source /path/to/Trexon.Web/artifacts
```

Добавьте NuGet.org отдельным restore source для транзитивных зависимостей, если ваш
проект ещё не восстановил их. Для разработки удобнее ProjectReference.

## До стабильного релиза

Сначала стабилизируйте API по опыту использования примера и внешнего приложения.
Запустите проверки из [Development](DEVELOPMENT.md), проверьте содержимое пакета и
потребление именно nupkg в отдельном проекте. Внесите реальную версию в csproj,
обновите статус и инструкции установки, оформите changelog и совместимость.

Тег, push, GitHub Release и публикация в NuGet — отдельные действия владельца проекта.
CI собирает артефакты, но сам не публикует пакет. Передача NuGet API key в исходники
или лог не требуется и не предусмотрена.
