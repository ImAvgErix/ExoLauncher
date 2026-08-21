using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExoLauncher.Services;
using Xunit;

namespace ExoLauncher.Tests;

public sealed class ExoPasswordAccountTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task CreatePasswordAccount_PersistsOnlyTheHeaderSession_AndReturnsNoSecrets()
    {
        const string password = "correct-horse-battery-staple";
        var root = Path.Combine(Path.GetTempPath(), "exo-password-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ExoSessionStore(Path.Combine(root, ExoSessionStore.FileName));
            var handler = new PasswordIdentityHandler();
            using var service = new ExoAccountService(
                store,
                handler,
                _ => throw new InvalidOperationException("browser"),
                () => throw new InvalidOperationException("listener"),
                origin: "http://127.0.0.1");

            var result = await service.CreatePasswordAccountAsync(
                "  Erix  ",
                "  USER@example.com  ",
                password,
                settings: null);
            var json = JsonSerializer.Serialize(result, JsonOptions);

            Assert.Equal(
                [ExoIdContract.PasswordSignUpPath, ExoIdContract.PasswordSignInPath],
                handler.PasswordPaths);
            Assert.Equal("POST", handler.LastMethod);
            Assert.Equal("application/json", handler.LastContentType);
            Assert.True(handler.SawStrictSignUpBody);
            Assert.True(handler.SawStrictSignInBody);
            Assert.True(handler.SawExpectedPassword);
            Assert.Equal("Bearer " + handler.HeaderToken, handler.MeAuthorization);
            Assert.Contains("\"ok\":true", json, StringComparison.Ordinal);
            Assert.Contains("\"signedIn\":true", json, StringComparison.Ordinal);
            Assert.Contains("\"provider\":\"password\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain(password, json, StringComparison.Ordinal);
            Assert.DoesNotContain(handler.HeaderToken, json, StringComparison.Ordinal);
            Assert.DoesNotContain(handler.BodyToken, json, StringComparison.Ordinal);

            var saved = store.TryLoad();
            Assert.NotNull(saved);
            Assert.Equal(handler.HeaderToken, saved!.AccessToken);
            Assert.NotEqual(handler.SignUpHeaderToken, saved.AccessToken);
            Assert.NotEqual(handler.BodyToken, saved.AccessToken);
            Assert.True(string.IsNullOrEmpty(saved.RefreshToken));
            Assert.Equal("password", saved.Provider);

            var protectedBytes = File.ReadAllBytes(store.Path);
            var protectedText = Encoding.UTF8.GetString(protectedBytes);
            Assert.DoesNotContain(password, protectedText, StringComparison.Ordinal);
            Assert.DoesNotContain(handler.HeaderToken, protectedText, StringComparison.Ordinal);
            var plaintext = ExoDpapi.Unprotect(protectedBytes);
            try
            {
                var sessionJson = Encoding.UTF8.GetString(plaintext);
                Assert.DoesNotContain(password, sessionJson, StringComparison.Ordinal);
                Assert.DoesNotContain("\"password\":", sessionJson, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort test cleanup */ }
        }
    }

    [Fact]
    public async Task PasswordSignIn_UsesTheOfficialEndpointAndStrictBody()
    {
        var root = Path.Combine(Path.GetTempPath(), "exo-password-signin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ExoSessionStore(Path.Combine(root, ExoSessionStore.FileName));
            var handler = new PasswordIdentityHandler();
            using var service = CreateService(store, handler);

            var result = await service.SignInWithPasswordAsync(
                " USER@example.com ", "correct-horse-battery-staple", settings: null);
            var json = JsonSerializer.Serialize(result, JsonOptions);

            Assert.Equal(ExoIdContract.PasswordSignInPath, handler.LastPath);
            Assert.True(handler.SawStrictSignInBody);
            Assert.Equal("user@example.com", handler.ReceivedEmail);
            Assert.Contains("\"signedIn\":true", json, StringComparison.Ordinal);
            Assert.Contains("\"providers\":[\"password\"]", json, StringComparison.Ordinal);
            Assert.DoesNotContain(handler.HeaderToken, json, StringComparison.Ordinal);
            Assert.Equal(handler.HeaderToken, store.TryLoad()!.AccessToken);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort test cleanup */ }
        }
    }

    [Theory]
    [InlineData("", "user@example.com", "correct-horse-battery-staple", "INVALID_REQUEST")]
    [InlineData("bad\u0001name", "user@example.com", "correct-horse-battery-staple", "INVALID_REQUEST")]
    [InlineData("Erix", "not-an-email", "correct-horse-battery-staple", "INVALID_REQUEST")]
    [InlineData("Erix", "user@example.com", "only-eleven", "INVALID_PASSWORD")]
    public async Task CreatePasswordAccount_RejectsInvalidBoundsBeforeNetwork(
        string name,
        string email,
        string password,
        string expectedCode)
    {
        var store = new ExoSessionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        var handler = new PasswordIdentityHandler();
        using var service = CreateService(store, handler);

        var json = JsonSerializer.Serialize(
            await service.CreatePasswordAccountAsync(name, email, password, settings: null),
            JsonOptions);

        Assert.Contains("\"ok\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"" + expectedCode + "\"", json, StringComparison.Ordinal);
        Assert.Equal(0, handler.PasswordCallCount);
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public async Task PasswordAuthentication_RejectsOverlongInputsBeforeNetwork()
    {
        var store = new ExoSessionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        var handler = new PasswordIdentityHandler();
        using var service = CreateService(store, handler);

        var longName = JsonSerializer.Serialize(
            await service.CreatePasswordAccountAsync(
                new string('n', 81),
                "user@example.com",
                "correct-horse-battery-staple",
                settings: null),
            JsonOptions);
        var longPassword = JsonSerializer.Serialize(
            await service.SignInWithPasswordAsync(
                "user@example.com",
                new string('p', 129),
                settings: null),
            JsonOptions);

        Assert.Contains("\"code\":\"INVALID_REQUEST\"", longName, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"INVALID_PASSWORD\"", longPassword, StringComparison.Ordinal);
        Assert.Equal(0, handler.PasswordCallCount);
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public async Task PasswordAuthentication_RejectsAnOversizedEncodedBodyBeforeNetwork()
    {
        var store = new ExoSessionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        var handler = new PasswordIdentityHandler();
        using var service = CreateService(store, handler);
        var largeButIndividuallyBoundedEmail = new string('\u00E9', 240) + "@x.co";

        var json = JsonSerializer.Serialize(
            await service.SignInWithPasswordAsync(
                largeButIndividuallyBoundedEmail,
                new string('\u00E9', 128),
                settings: null),
            JsonOptions);

        Assert.Contains("\"code\":\"INVALID_REQUEST\"", json, StringComparison.Ordinal);
        Assert.Contains("too large", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.PasswordCallCount);
        Assert.Null(store.TryLoad());
    }

    [Theory]
    [InlineData(false, 409, "ACCOUNT_CONFLICT", "The account request could not be completed.")]
    [InlineData(true, 401, "INVALID_CREDENTIALS", "The email or password is incorrect.")]
    [InlineData(true, 429, "RATE_LIMITED", "Too many attempts. Try again in 37 seconds.")]
    public async Task PasswordAuthentication_MapsServerFailuresWithoutEchoingTheBody(
        bool signIn,
        int status,
        string code,
        string expectedMessage)
    {
        const string password = "correct-horse-battery-staple";
        var store = new ExoSessionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        var handler = new PasswordIdentityHandler
        {
            ResponseStatus = (HttpStatusCode)status,
            ErrorCode = code,
            ErrorMessage = "unsafe user@example.com " + password,
        };
        using var service = CreateService(store, handler);

        var result = signIn
            ? await service.SignInWithPasswordAsync("user@example.com", password, settings: null)
            : await service.CreatePasswordAccountAsync("Erix", "user@example.com", password, settings: null);
        var json = JsonSerializer.Serialize(result, JsonOptions);

        Assert.Contains("\"code\":\"" + code + "\"", json, StringComparison.Ordinal);
        Assert.Contains(expectedMessage, json, StringComparison.Ordinal);
        Assert.DoesNotContain(password, json, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", json, StringComparison.Ordinal);
        Assert.Equal(
            signIn ? ExoIdContract.PasswordSignInPath : ExoIdContract.PasswordSignUpPath,
            handler.PasswordPaths.Single());
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public async Task PasswordAuthentication_RejectsSuccessWithoutTheBearerHeader()
    {
        var store = new ExoSessionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        var handler = new PasswordIdentityHandler { IncludeSessionHeader = false };
        using var service = CreateService(store, handler);

        var json = JsonSerializer.Serialize(
            await service.CreatePasswordAccountAsync(
                "Erix", "user@example.com", "correct-horse-battery-staple", settings: null),
            JsonOptions);

        Assert.Contains("\"ok\":false", json, StringComparison.Ordinal);
        Assert.Contains("did not return a valid session", json, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.BodyToken, json, StringComparison.Ordinal);
        Assert.Equal(2, handler.PasswordCallCount);
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public async Task PasswordAuthentication_CancellationReturnsQuietlyWithoutSavingASecret()
    {
        var store = new ExoSessionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        var handler = new PasswordIdentityHandler { WaitUntilCancelled = true };
        using var service = CreateService(store, handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        var json = JsonSerializer.Serialize(
            await service.SignInWithPasswordAsync(
                "user@example.com", "correct-horse-battery-staple", settings: null, cancellation.Token),
            JsonOptions);

        Assert.Contains("\"ok\":false", json, StringComparison.Ordinal);
        Assert.Contains("Sign-in was cancelled.", json, StringComparison.Ordinal);
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public async Task PasswordAuthentication_PreCancelledCallReturnsQuietlyWithoutNetwork()
    {
        var store = new ExoSessionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        var handler = new PasswordIdentityHandler();
        using var service = CreateService(store, handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var json = JsonSerializer.Serialize(
            await service.SignInWithPasswordAsync(
                "user@example.com", "correct-horse-battery-staple", settings: null, cancellation.Token),
            JsonOptions);

        Assert.Contains("Sign-in was cancelled.", json, StringComparison.Ordinal);
        Assert.Equal(0, handler.PasswordCallCount);
        Assert.Null(store.TryLoad());
    }

    [Fact]
    public async Task PasswordAuthentication_NetworkErrorsNeverEchoCredentialBearingExceptions()
    {
        const string password = "correct-horse-battery-staple";
        var store = new ExoSessionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"));
        var handler = new PasswordIdentityHandler { ThrowCredentialBearingNetworkError = true };
        using var service = CreateService(store, handler);

        var json = JsonSerializer.Serialize(
            await service.SignInWithPasswordAsync("user@example.com", password, settings: null),
            JsonOptions);

        Assert.Contains("identity service could not be reached", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(password, json, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", json, StringComparison.Ordinal);
        Assert.Null(store.TryLoad());
    }

    private static ExoAccountService CreateService(ExoSessionStore store, HttpMessageHandler handler) =>
        new(
            store,
            handler,
            _ => throw new InvalidOperationException("browser"),
            () => throw new InvalidOperationException("listener"),
            origin: "http://127.0.0.1");

    private sealed class PasswordIdentityHandler : HttpMessageHandler
    {
        public string HeaderToken { get; } = "header-session-token-fixture";
        public string SignUpHeaderToken { get; } = "signup-session-token-must-not-persist";
        public string BodyToken { get; } = "body-session-token-must-not-win";
        public string? LastPath { get; private set; }
        public string? LastMethod { get; private set; }
        public string? LastContentType { get; private set; }
        public string? MeAuthorization { get; private set; }
        public bool SawStrictSignUpBody { get; private set; }
        public bool SawStrictSignInBody { get; private set; }
        public bool SawExpectedPassword { get; private set; }
        public string? ReceivedEmail { get; private set; }
        public bool IncludeSessionHeader { get; init; } = true;
        public bool IncludeSignUpSessionHeader { get; init; } = true;
        public bool WaitUntilCancelled { get; init; }
        public bool ThrowCredentialBearingNetworkError { get; init; }
        public HttpStatusCode ResponseStatus { get; init; } = HttpStatusCode.OK;
        public string ErrorCode { get; init; } = "INVALID_REQUEST";
        public string ErrorMessage { get; init; } = "Request failed.";
        public int PasswordCallCount { get; private set; }
        public List<string> PasswordPaths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath.TrimEnd('/') ?? "";
            if (path.Equals(ExoIdContract.PasswordSignUpPath, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(ExoIdContract.PasswordSignInPath, StringComparison.OrdinalIgnoreCase))
            {
                PasswordCallCount++;
                PasswordPaths.Add(path);
                LastPath = path;
                LastMethod = request.Method.Method;
                LastContentType = request.Content?.Headers.ContentType?.MediaType;
                await using var body = await request.Content!.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
                var root = document.RootElement;
                var keys = root.EnumerateObject().Select(property => property.Name).Order().ToArray();
                if (path.Equals(ExoIdContract.PasswordSignUpPath, StringComparison.OrdinalIgnoreCase))
                    SawStrictSignUpBody = keys.SequenceEqual(new[] { "email", "name", "password" });
                else
                    SawStrictSignInBody = keys.SequenceEqual(new[] { "email", "password" });
                SawExpectedPassword = root.GetProperty("password").GetString() == "correct-horse-battery-staple";
                ReceivedEmail = root.GetProperty("email").GetString();
                if (WaitUntilCancelled)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                if (ThrowCredentialBearingNetworkError)
                    throw new HttpRequestException("user@example.com correct-horse-battery-staple");
                if (ResponseStatus != HttpStatusCode.OK)
                {
                    var failed = Json(ResponseStatus, JsonSerializer.Serialize(new
                    {
                        error = new { code = ErrorCode, message = ErrorMessage },
                    }));
                    if (ResponseStatus == HttpStatusCode.TooManyRequests)
                        failed.Headers.TryAddWithoutValidation("X-Retry-After", "37");
                    return failed;
                }
                var response = Json(HttpStatusCode.OK,
                    "{\"token\":\"" + BodyToken +
                    "\",\"user\":{\"id\":\"acc_password\",\"name\":\"Erix\",\"email\":\"user@example.com\"}}");
                var isSignUp = path.Equals(
                    ExoIdContract.PasswordSignUpPath,
                    StringComparison.OrdinalIgnoreCase);
                if (isSignUp ? IncludeSignUpSessionHeader : IncludeSessionHeader)
                {
                    response.Headers.TryAddWithoutValidation(
                        ExoIdContract.BearerSessionHeader,
                        isSignUp ? SignUpHeaderToken : HeaderToken);
                }
                return response;
            }

            if (path.Equals(ExoIdContract.MePath, StringComparison.OrdinalIgnoreCase))
            {
                MeAuthorization = request.Headers.Authorization?.ToString();
                return Json(HttpStatusCode.OK,
                    "{\"id\":\"acc_password\",\"name\":\"Erix\",\"email\":\"user@example.com\",\"handle\":null," +
                    "\"session\":{\"id\":\"session_1\",\"expiresAt\":\"2030-08-25T00:00:00.000Z\"}}");
            }

            if (path.Equals(ExoIdContract.HealthPath, StringComparison.OrdinalIgnoreCase))
            {
                return Json(HttpStatusCode.OK,
                    "{\"ok\":true,\"service\":\"exo-id\",\"capabilities\":{\"providers\":{\"google\":false,\"email\":false,\"password\":true}}}");
            }

            return Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"NOT_FOUND\",\"message\":\"Not found.\"}}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }
}
