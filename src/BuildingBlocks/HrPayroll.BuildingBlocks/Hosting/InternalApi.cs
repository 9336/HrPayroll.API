using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HrPayroll.BuildingBlocks.Hosting;

/// <summary>
/// Service-to-service calls ("InternalApi" section). Internal endpoints live under /internal, are never routed by the
/// gateway, and require the shared key in the X-Internal-Key header.
/// </summary>
public class InternalApiOptions
{
    public const string Section = "InternalApi";
    public const string Header = "X-Internal-Key";

    public string Key { get; set; } = "";
}

/// <summary>Only other services (with the internal key) may call the endpoint.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class InternalOnlyAttribute : Attribute, IAuthorizationFilter, IAllowAnonymous
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var key = context.HttpContext.RequestServices.GetRequiredService<IOptions<InternalApiOptions>>().Value.Key;
        var sent = context.HttpContext.Request.Headers[InternalApiOptions.Header].ToString();
        if (string.IsNullOrEmpty(key) || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(sent)))
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
    }
}

public static class InternalClientExtensions
{
    /// <summary>
    /// Named HttpClient for another service: base address from "Services:{name}", internal key added to every call.
    /// </summary>
    public static IHttpClientBuilder AddInternalClient(this IServiceCollection services, IConfiguration config, string name) =>
        services.AddHttpClient(name, c =>
        {
            c.BaseAddress = new Uri(config[$"Services:{name}"] ?? throw new InvalidOperationException($"Services:{name} is not configured."));
            c.DefaultRequestHeaders.Add(InternalApiOptions.Header, config[$"{InternalApiOptions.Section}:Key"]);
            c.Timeout = TimeSpan.FromSeconds(15);
        });
}
