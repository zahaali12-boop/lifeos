using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quicker.Kernel.Time;

namespace Quicker.Storage;

public static class StorageRegistration
{
    /// <summary>Registers <see cref="IObjectStorage"/> for the configured provider (section <c>Quicker:Storage</c>).</summary>
    public static IServiceCollection AddQuickerStorage(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.AddSingleton(static sp => sp.GetRequiredService<IOptions<StorageOptions>>().Value);
        services.AddSingleton<IObjectStorage>(static sp =>
        {
            var options = sp.GetRequiredService<StorageOptions>();
            var clock = sp.GetRequiredService<IClock>();
            return options.Provider.ToLowerInvariant() switch
            {
                "filesystem" => new FileSystemObjectStorage(options.Path, clock),
                "s3" => new S3ObjectStorage(CreateS3Client(options), options, clock),
                _ => throw new InvalidOperationException($"Unknown storage provider '{options.Provider}'. Supported: filesystem, s3."),
            };
        });
        return services;
    }

    public static IAmazonS3 CreateS3Client(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var config = new AmazonS3Config { ForcePathStyle = options.ForcePathStyle };
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            config.ServiceURL = options.Endpoint;
            config.AuthenticationRegion = options.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return string.IsNullOrEmpty(options.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }
}
