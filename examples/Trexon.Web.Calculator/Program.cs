using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Trexon.Web;

// Всё приложение — в этом файле. Запуск из корня репозитория:
// dotnet run --project examples/Trexon.Web.Calculator
var web = TrexonApp.Create(args, api => api.Title = "Calculator API");
web.AddRoutes<CalculatorRoutes>();
// Указываем только порт: http://127.0.0.1:5081. Swagger уже доступен по /docs.
await web.RunAsync(5081);

// Обычный C#-класс: Routes задаёт префикс, Tag — группу операций в Swagger.
[Routes("/calculator"), Tag("Calculator")]
public sealed class CalculatorRoutes
{
    // GET /calculator/add?a=10&b=5 → {"operation":"Add","a":10,"b":5,"result":15}.
    // operation берётся из пути; enum даёт список допустимых операций в Swagger.
    [Get("{operation}"), Summary("Calculate two numbers"), Response(200, typeof(Calculation)), Response(400)]
    public ValueTask<Calculation> Calculate(
        Operation operation,
        // BindRequired требует наличие обоих query-параметров, Range проверяет границы.
        // Ошибки привязки/валидации автоматически становятся ответом 400 с Problem Details.
        [BindRequired, Range(-1_000_000d, 1_000_000d)] double a,
        [BindRequired, Range(-1_000_000d, 1_000_000d)] double b)
    {
        var result = operation switch
        {
            Operation.Add => a + b,
            Operation.Subtract => a - b,
            Operation.Multiply => a * b,
            Operation.Divide when b == 0 => throw new HttpError(400, "Cannot divide by zero."),
            Operation.Divide => a / b,
            _ => throw new HttpError(400, "Unknown operation.")
        };
        // Даже при конечных входных значениях деление на очень малое число может дать Infinity.
        if (!double.IsFinite(result)) throw new HttpError(400, "Result is outside the supported numeric range.");
        // Вычисление не требует I/O: готовый ValueTask без Task.Run и искусственных задержек.
        // Возвращаемый объект библиотека сериализует в JSON и описывает в OpenAPI.
        return ValueTask.FromResult(new Calculation(operation, a, b, result));
    }
}

public enum Operation { Add, Subtract, Multiply, Divide }
public sealed record Calculation(Operation Operation, double A, double B, double Result);
