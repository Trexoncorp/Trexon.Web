# Валидация запросов

[Оглавление](README.md) · [Ошибки](ERRORS.md)

## Стандартные атрибуты

`[Routes]` включает поведение API controller. JSON десериализуется и проверяется
до вызова обработчика. DataAnnotations работают на свойствах, параметрах и вложенных
моделях; также поддерживается `IValidatableObject` средствами MVC.

```csharp
public sealed class CreateProduct
{
    [Required, StringLength(120, MinimumLength = 3)]
    public string Name { get; set; } = "";

    [Range(0.01, 1000000)]
    public decimal Price { get; set; }
}
```

Нужен `using System.ComponentModel.DataAnnotations;`. Отсутствующая обязательная
модель, неверный тип, битый JSON и ошибки полей возвращают HTTP 400:

```json
{
  "type": "https://httpstatuses.io/400",
  "title": "Request validation failed",
  "status": 400,
  "instance": "/products",
  "errors": { "Name": ["The Name field is required."] },
  "traceId": "..."
}
```

Текст сообщений зависит от атрибутов и настроек локализации; клиент должен опираться
на статус и ключи `errors`, а не разбирать английский текст.

## Свои асинхронные правила

<!-- verify: custom-validator -->
```csharp
using System.ComponentModel.DataAnnotations;
using Trexon.Web;

var app = TrexonApp.Create(args);
app.AddRoutes<ProductRoutes>();
app.AddValidator<CreateProduct, ProductValidator>();
await app.RunAsync();

public sealed class CreateProduct
{
    [Required] public string Name { get; set; } = "";
}

public sealed class ProductValidator : IRequestValidator<CreateProduct>
{
    public ValueTask<IEnumerable<ValidationIssue>> ValidateAsync(CreateProduct model, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IEnumerable<ValidationIssue> errors = model.Name.Trim().Length < 3
            ? [new("Name", "Use at least three non-space characters.")] : [];
        return ValueTask.FromResult(errors);
    }
}

[Routes("/products")]
public sealed class ProductRoutes
{
    [Post] public ValueTask<CreateProduct> Create(CreateProduct request) => ValueTask.FromResult(request);
}
```

Валидаторы регистрируются как scoped и получают зависимости через конструктор.
Можно зарегистрировать несколько валидаторов одного типа: ошибки объединятся.
Они выполняются последовательно после binding/DataAnnotations, до обработчика.
Токен запроса передаётся в `ValidateAsync` и далее в I/O приложения.

Собственный валидатор применяется к аргументу подходящего типа, а не автоматически
к каждому вложенному объекту. Проверку вложенных бизнес-объектов организуйте внутри
валидатора родительской модели. Не проверяйте уникальность только предварительным
SELECT: параллельные записи требуют ограничения уникальности и обработки конфликта в БД.
