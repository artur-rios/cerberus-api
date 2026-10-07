using ArturRios.Cerberus.WebApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;

if (args.Length != 1) throw new ArgumentException("Exactly one output path is required.");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ApplicationName = typeof(Startup).Assembly.GetName().Name,
    Args = []
});
builder.Services.AddCerberusContracts();
using var host = builder.Build();
var document = host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
using var output = File.CreateText(args[0]);
document.SerializeAsV3(new OpenApiJsonWriter(output));
