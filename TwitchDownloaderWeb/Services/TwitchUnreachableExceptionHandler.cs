using Microsoft.AspNetCore.Diagnostics;

namespace TwitchDownloaderWeb.Services
{
    /// <summary>
    /// Turns errors the endpoints don't handle themselves into answers the page can show. Twitch being unreachable or too slow
    /// (an HttpClient timeout is a TaskCanceledException wrapping a TimeoutException) → 502 "Couldn't reach Twitch" instead of a 500.
    /// A request the browser gave up on (the player seeking, a page closed) is ended quietly instead of being logged as an error.
    /// The exception message is never sent back: it could name a Twitch URL with a signed token.
    /// </summary>
    public sealed class TwitchUnreachableExceptionHandler(ILogger<TwitchUnreachableExceptionHandler> logger) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            {
                // Nobody is listening for an answer
                context.Response.StatusCode = 499;
                return true;
            }

            if (!IsTwitchUnreachable(exception))
                return false;

            logger.LogWarning("Twitch didn't answer {Method} {Path}: {Kind}", context.Request.Method, context.Request.Path, exception.GetType().Name);
            if (context.Response.HasStarted)
                return true;

            await Results.Problem(detail: "Couldn't reach Twitch. Try again in a moment.", statusCode: StatusCodes.Status502BadGateway).ExecuteAsync(context);
            return true;
        }

        /// <summary>A Twitch request that failed or timed out (not one cancelled on purpose).</summary>
        public static bool IsTwitchUnreachable(Exception exception) =>
            exception is HttpRequestException
            || (exception is TaskCanceledException { InnerException: TimeoutException });
    }
}
