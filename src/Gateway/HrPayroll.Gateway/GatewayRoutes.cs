using Yarp.ReverseProxy.Configuration;

namespace HrPayroll.Gateway;

/// <summary>
/// Which service answers which URL. The web and mobile apps keep calling the same URLs they called on the single API;
/// /internal/* (service-to-service) is never exposed.
/// </summary>
public static class GatewayRoutes
{
    /// <summary>Service -> path prefixes (each also matches everything below it).</summary>
    private static readonly (string Service, string[] Paths)[] Map =
    [
        ("identity", ["/api/auth", "/api/roles"]),
        ("employees", ["/api/employees", "/api/departments", "/api/company", "/api/org", "/api/holidays", "/api/employee-uploads"]),
        ("leave", ["/api/leaves", "/api/year-end/leave-year", "/api/year-end/leave-rollover"]),
        ("attendance", ["/api/calendar", "/api/attendance-locations", "/api/employee-faces"]),
        ("payroll", ["/api/salary", "/api/payroll", "/api/payslips", "/api/documents", "/api/promotions", "/api/year-end/financial-years"]),
        ("expenses", ["/api/expenses", "/api/travel-claims"]),
        ("performance", ["/api/appraisals", "/api/feedback", "/api/tasks"]),
        ("recruitment", ["/api/recruitment", "/api/assessments", "/api/public/tests"]),
        ("helpdesk", ["/api/it-requests", "/api/visits"]),
        ("chat", ["/api/chat", "/hubs/chat"]),
        ("notifications", ["/api/emails", "/api/email-settings"]),
    ];

    public static IReadOnlyList<string> Services => Map.Select(m => m.Service).ToList();

    public static IReadOnlyList<RouteConfig> Routes()
    {
        var routes = Map.SelectMany(m => m.Paths.Select(path => new RouteConfig
        {
            RouteId = $"{m.Service}{path.Replace('/', '-')}",
            ClusterId = m.Service,
            Match = new RouteMatch { Path = path + "/{**rest}" },
        })).ToList();

        // Logins live in the Identity service, so this employees URL goes there (more specific route wins).
        routes.Add(new RouteConfig
        {
            RouteId = "identity-reset-password",
            ClusterId = "identity",
            Order = -1,
            Match = new RouteMatch { Path = "/api/employees/{id:int}/reset-password" },
        });
        return routes;
    }

    public static IReadOnlyList<ClusterConfig> Clusters(IReadOnlyDictionary<string, string> addresses) =>
        Services.Select(s => new ClusterConfig
        {
            ClusterId = s,
            Destinations = new Dictionary<string, DestinationConfig>
            {
                [s] = new() { Address = addresses.TryGetValue(s, out var a) ? a : throw new InvalidOperationException($"Services:{s} is not configured.") },
            },
        }).ToList();
}
