using System.Globalization;
using Centrifugal.Cent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers a <see cref="CentClient"/> with dependency injection, its HTTP client made by
/// <c>IHttpClientFactory</c>: the returned <see cref="IHttpClientBuilder"/> adds handlers to
/// it - tracing, logging, resilience.
/// </summary>
/// <remarks>
/// A resilience handler which retries requests would publish again a publication whose
/// reply was lost - twice on the NATS broker, which ignores idempotency keys - and its
/// timeouts would cut the SDK's deadline short. Disable its retries of <c>POST</c>, as
/// <c>.AddStandardResilienceHandler(o =&gt; o.Retry.DisableForUnsafeHttpMethods())</c> does,
/// and retry with <see cref="Retry"/> and idempotency keys instead.
/// </remarks>
public static class CentServiceCollectionExtensions
{
    /// <summary>The name of the client's HTTP client in <c>IHttpClientFactory</c>.</summary>
    public const string HttpClientName = "Centrifugal.Cent";

    /// <summary>
    /// Registers one <see cref="CentClient"/> for the application, configured from a section
    /// of the application's configuration - then by <paramref name="configure"/>, for what
    /// configuration cannot hold. It is closed when the application stops: the calls in
    /// progress end first.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">
    /// A section such as <c>builder.Configuration.GetSection("Centrifugo")</c>, with the keys
    /// <c>ApiEndpoint</c>, <c>ApiKey</c>, <c>RequestTimeout</c> (<c>00:00:10</c>),
    /// <c>Headers</c>, and <c>AutoBatch:MaxInFlight</c> and <c>AutoBatch:MaxBatchSize</c>.
    /// </param>
    /// <param name="configure">Sets the other options, with the application's services at hand.</param>
    /// <returns>The client's HTTP client, for its handlers.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddCentClient(builder.Configuration.GetSection("Centrifugo"));
    /// </code>
    /// With <c>appsettings.json</c>:
    /// <code>
    /// { "Centrifugo": { "ApiEndpoint": "http://localhost:8000/api", "ApiKey": "...", "AutoBatch": { "MaxInFlight": 32 } } }
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddCentClient(this IServiceCollection services, IConfiguration configuration, Action<IServiceProvider, CentClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var options = services.AddOptions<CentClientOptions>().Configure(o => Apply(configuration, o));
        if (configure is not null)
        {
            options.Configure<IServiceProvider>((o, provider) => configure(provider, o));
        }
        return AddClient(services);
    }

    /// <summary>
    /// Registers one <see cref="CentClient"/> for the application, closed when the
    /// application stops: the calls in progress end first.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configure">Sets the client's options, with the application's services at hand.</param>
    /// <returns>The client's HTTP client, for its handlers.</returns>
    public static IHttpClientBuilder AddCentClient(this IServiceCollection services, Action<IServiceProvider, CentClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<CentClientOptions>().Configure<IServiceProvider>((o, provider) => configure(provider, o));
        return AddClient(services);
    }

    /// <summary>Registers one <see cref="CentClient"/> for the application, closed when the application stops.</summary>
    /// <param name="services">The services.</param>
    /// <param name="configure">Sets the client's options.</param>
    /// <returns>The client's HTTP client, for its handlers.</returns>
    public static IHttpClientBuilder AddCentClient(this IServiceCollection services, Action<CentClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddCentClient((_, options) => configure(options));
    }

    private static IHttpClientBuilder AddClient(IServiceCollection services)
    {
        // The client lives as long as the application: so does its handler, the SDK's
        // default one, keeping its connections alive.
        var http = services.AddHttpClient(HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(ClientCore.CreateHandler)
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        services.TryAddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<CentClientOptions>>().Value;
            if (options.HttpClient is null)
            {
                options = options.WithHttpClient(provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName));
            }
            return new CentClient(options);
        });
        return http;
    }

    /// <summary>Sets the options configuration holds; those it does not mention keep their values.</summary>
    private static void Apply(IConfiguration configuration, CentClientOptions options)
    {
        options.ApiEndpoint = configuration["ApiEndpoint"] ?? options.ApiEndpoint;
        options.ApiKey = configuration["ApiKey"] ?? options.ApiKey;
        if (configuration["RequestTimeout"] is { } timeout)
        {
            options.RequestTimeout = TimeSpan.TryParse(timeout, CultureInfo.InvariantCulture, out var t)
                ? t
                : throw new CentInvalidConfigException($"RequestTimeout \"{timeout}\" is not a time span: write it as 00:00:10");
        }
        var headers = configuration.GetSection("Headers").GetChildren().Where(h => h.Value is not null).ToList();
        if (headers.Count > 0)
        {
            options.Headers = new Dictionary<string, string>(options.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers)
            {
                options.Headers[h.Key] = h.Value!;
            }
        }
        var autoBatch = configuration.GetSection("AutoBatch");
        if (autoBatch.Exists())
        {
            options.AutoBatch = new AutoBatchOptions
            {
                MaxInFlight = Int(autoBatch, "MaxInFlight"),
                MaxBatchSize = Int(autoBatch, "MaxBatchSize"),
            };
        }
    }

    private static int Int(IConfiguration section, string key) => section[key] switch
    {
        null => 0,
        var v when int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        var v => throw new CentInvalidConfigException($"AutoBatch:{key} \"{v}\" is not an integer"),
    };
}
