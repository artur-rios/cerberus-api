namespace ArturRios.Cerberus.WebApi.Middleware;

public sealed class RequestLimitMiddleware(RequestDelegate next, long maximum)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.ContentLength > maximum)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var original = context.Request.Body;
        context.Request.Body = new BoundedRequestStream(original, maximum);
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
        finally
        {
            context.Request.Body = original;
        }
    }
}
