using System.ComponentModel.DataAnnotations;
using Trexon.Configuration;

namespace Trexon.Web.Sample;

public sealed class SampleSettings
{
    [Env("TREXON_URL")] public string Url { get; init; } = "http://127.0.0.1:5080";
    [Env("TREXON_DATA_DIR")] public string DataDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "data");
    [Env("TREXON_JWT_KEY"), Secret] public string JwtKey { get; init; } = "";
    [Env("TREXON_API_KEY"), Secret] public string ApiKey { get; init; } = "";
    [Env("TREXON_ADMIN_PASSWORD"), Secret] public string AdminPassword { get; init; } = "";
    [Env("TREXON_FRONTEND_ORIGIN")] public string FrontendOrigin { get; init; } = "http://localhost:5173";
}
