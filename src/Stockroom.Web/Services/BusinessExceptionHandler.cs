using Microsoft.AspNetCore.Diagnostics;
using Stockroom.Web.Domain;

namespace Stockroom.Web.Services;

public sealed class BusinessExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not BusinessException error) return false;
        context.Response.StatusCode = error.StatusCode;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new() { Status = error.StatusCode, Title = error.Title, Detail = error.Message }
        });
    }
}
