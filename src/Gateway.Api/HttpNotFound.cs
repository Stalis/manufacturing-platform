using System.Net;

namespace Gateway.Api;

public static class HttpNotFound
{
    public static async Task<T?> OrNotFound<T>(this Task<T> call) where T : class
    {
        try
        {
            return await call;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
