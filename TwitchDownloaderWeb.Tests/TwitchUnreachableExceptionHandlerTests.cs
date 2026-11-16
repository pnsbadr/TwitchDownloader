using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TwitchDownloaderWeb.Services;

namespace TwitchDownloaderWeb.Tests
{
    public class TwitchUnreachableExceptionHandlerTests
    {
        private static DefaultHttpContext NewContext()
        {
            var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
            return new DefaultHttpContext { RequestServices = services, Response = { Body = new MemoryStream() } };
        }

        private static readonly TwitchUnreachableExceptionHandler Handler = new(NullLogger<TwitchUnreachableExceptionHandler>.Instance);

        [Fact]
        public async Task TwitchTimeoutIs502()
        {
            var context = NewContext();
            var handled = await Handler.TryHandleAsync(context, new TaskCanceledException("timed out", new TimeoutException()), TestContext.Current.CancellationToken);
            Assert.True(handled);
            Assert.Equal(502, context.Response.StatusCode);
        }

        [Fact]
        public async Task TwitchErrorIs502WithoutItsMessage()
        {
            var context = NewContext();
            var handled = await Handler.TryHandleAsync(context, new HttpRequestException("https://usher.example/secret-token"), TestContext.Current.CancellationToken);
            Assert.True(handled);
            Assert.Equal(502, context.Response.StatusCode);
            context.Response.Body.Position = 0;
            var body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("secret-token", body);
        }

        [Fact]
        public async Task OtherErrorsAreLeftAlone()
        {
            Assert.False(await Handler.TryHandleAsync(NewContext(), new InvalidOperationException(), TestContext.Current.CancellationToken));
            // Cancelled without a timeout and without the browser leaving: not ours to name
            Assert.False(await Handler.TryHandleAsync(NewContext(), new TaskCanceledException(), TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task AbortedRequestEndsQuietly()
        {
            var context = NewContext();
            using var aborted = new CancellationTokenSource();
            aborted.Cancel();
            context.RequestAborted = aborted.Token;
            Assert.True(await Handler.TryHandleAsync(context, new OperationCanceledException(aborted.Token), TestContext.Current.CancellationToken));
            Assert.Equal(499, context.Response.StatusCode);
        }
    }
}
