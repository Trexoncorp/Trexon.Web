using System.ComponentModel.DataAnnotations;
using Trexon.Configuration;

namespace TrexonApi;

[EnvPrefix("APP_")]
public sealed class AppSettings
{
    [Required]
    public string Name { get; init; } = "TrexonApi";

    [Range(1, 65535)]
    public int Port { get; init; } = 5090;

    [Required]
    public string DataDirectory { get; init; } = "data";

    [Required]
    public string LogDirectory { get; init; } = "logs";

    public Trexon.Logging.LogLevel LogLevel { get; init; } = Trexon.Logging.LogLevel.Information;
}
