namespace ArturRios.Cerberus.WebApi.Middleware;

[AttributeUsage(AttributeTargets.Method)]
public sealed class HealthEndpointAttribute : Attribute;
[AttributeUsage(AttributeTargets.Method)]
public sealed class OperationalAuthenticationAttribute : Attribute;
