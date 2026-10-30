// An ASP.NET Core backend for Centrifugo:
//
//   dotnet run --project examples/Backend
//
// Centrifugo calls /centrifugo/connect as its connect proxy, and reads the public key at
// /.well-known/jwks.json as client.token.jwks_public_endpoint; browsers get connection
// tokens from /centrifugo/connection_token. Keep the proxy reachable by Centrifugo only.
using System.Security.Cryptography;
using Centrifugal.Cent;
using Centrifugal.Cent.Tokens;
using Proxy = Centrifugal.Cent.Proxy;

var builder = WebApplication.CreateBuilder(args);
// From appsettings.json: {"Centrifugo": {"ApiEndpoint": "http://localhost:8000/api", "ApiKey": "..."}}
builder.Services.AddCentClient(builder.Configuration.GetSection("Centrifugo"));
var app = builder.Build();

// In production, load the key from a secret store: a key made at start changes on restart.
using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var key = SigningKey.ECDsa(ecdsa, keyId: "2026-10");

// A request a handler fails is logged under the category Centrifugal.Cent.
var proxy = new Proxy.ProxyHandlers();
app.MapCentrifugo("/centrifugo/connect", proxy.Connect((request, context) =>
{
    var user = context.Request.GetHttpContext().Request.Cookies["user"]
        ?? throw new Proxy.ProxyDisconnectException(4501, "unauthorized");
    return Task.FromResult(new Proxy.ConnectResult { User = user, Channels = [$"user:{user}"] });
}));

var endpoints = new EndpointHandlers();
app.MapCentrifugo("/centrifugo/connection_token", endpoints.ConnectionToken(context =>
{
    var user = context.Request.GetHttpContext().Request.Cookies["user"]
        ?? throw new EndpointUnauthorizedException();
    return Task.FromResult(Token.Connection(key, new ConnectionClaims
    {
        User = user,
        Expiry = Expiry.After(TimeSpan.FromMinutes(15)),
    }));
}));
app.MapCentrifugo("/.well-known/jwks.json", Jwks.Endpoint([key]));

app.MapPost("/notify/{user}", async (string user, CentClient client, CancellationToken ct) =>
{
    var result = await client.PublishAsync($"user:{user}", new { text = "ping" }, ct);
    return Results.Ok(result);
});

app.Run();
