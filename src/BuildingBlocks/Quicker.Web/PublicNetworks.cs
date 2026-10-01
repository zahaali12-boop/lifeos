using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Quicker.Web;

/// <summary>A request to a destination a tenant chose was refused because it is not on the public internet.</summary>
public sealed class NonPublicDestinationException(string host, IReadOnlyList<IPAddress> addresses)
    : IOException($"{host} is not a public internet address ({string.Join(", ", addresses)}); requests to private, loopback, link-local and reserved networks are refused.")
{
    public string Host { get; } = host;

    public IReadOnlyList<IPAddress> Addresses { get; } = addresses;
}

/// <summary>
/// Keeps requests to addresses a tenant chooses (webhook receivers, single sign-on providers) on the public internet, so
/// the server cannot be pointed at its own network: loopback, private ranges, link-local (cloud metadata at
/// 169.254.169.254), carrier-grade NAT, multicast, documentation and reserved ranges, and IPv6 forms that embed an IPv4
/// address. The check runs when the connection is opened, on the addresses actually connected to, so a name that
/// resolves differently later (DNS rebinding) or a redirect cannot get round it. Operators list internal networks that
/// may be reached anyway in <c>Quicker:Outbound:AllowedPrivateNetworks</c> (addresses or CIDR ranges, comma-separated).
/// </summary>
public sealed class PublicNetworkPolicy
{
    public const string AllowedPrivateNetworksKey = "Quicker:Outbound:AllowedPrivateNetworks";

    private static readonly IPNetwork[] NonPublic =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"), IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"), IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/4"), IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("::/96"), IPNetwork.Parse("::1/128"), IPNetwork.Parse("64:ff9b::/96"), IPNetwork.Parse("64:ff9b:1::/48"),
        IPNetwork.Parse("100::/64"), IPNetwork.Parse("2001::/32"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("fc00::/7"), IPNetwork.Parse("fe80::/10"), IPNetwork.Parse("fec0::/10"), IPNetwork.Parse("ff00::/8"),
    ];

    public PublicNetworkPolicy(IConfiguration configuration)
        : this(Parse(configuration?[AllowedPrivateNetworksKey]))
    {
    }

    public PublicNetworkPolicy(IReadOnlyList<IPNetwork> allowedPrivateNetworks) => AllowedPrivateNetworks = allowedPrivateNetworks;

    /// <summary>Internal networks the operator allows anyway (an on-premises integration host, a local test receiver).</summary>
    public IReadOnlyList<IPNetwork> AllowedPrivateNetworks { get; }

    public static IReadOnlyList<IPNetwork> Parse(string? list)
    {
        var networks = new List<IPNetwork>();
        foreach (var entry in (list ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(entry, out var network))
            {
                networks.Add(network);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                networks.Add(new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
            }
            else
            {
                throw new InvalidOperationException($"{AllowedPrivateNetworksKey} has an entry that is neither an address nor a CIDR range: '{entry}'.");
            }
        }

        return networks;
    }

    public bool Allows(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (AllowedPrivateNetworks.Any(n => n.Contains(candidate)))
        {
            return true;
        }

        if (NonPublic.Any(n => n.Contains(candidate)))
        {
            return false;
        }

        // Addresses scoped to an interface (fe80::1%eth0) are local whatever their prefix.
        return candidate.AddressFamily != AddressFamily.InterNetworkV6 || candidate.ScopeId == 0;
    }

    /// <summary>
    /// The addresses of <paramref name="host"/> that are refused, when every address it resolves to is; empty when it may
    /// be reached or cannot be resolved now (delivery checks again). For early feedback when an address is saved.
    /// </summary>
    public async Task<IReadOnlyList<IPAddress>> RefusedAddressesAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return [];
            }
        }

        return addresses.Length > 0 && !addresses.Any(Allows) ? addresses : [];
    }

    /// <summary>
    /// A handler that resolves the destination itself and connects only to allowed addresses. It never uses a proxy (the
    /// check must judge the real destination) and never follows redirects (a receiver that moves says so with its status).
    /// </summary>
    public SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectAsync,
    };

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
        var allowed = addresses.Where(Allows).ToList();
        if (allowed.Count == 0)
        {
            throw new NonPublicDestinationException(host, addresses);
        }

        SocketException? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last!;
    }
}

public static class PublicNetworkRegistration
{
    /// <summary>Registers the policy (once) from <see cref="PublicNetworkPolicy.AllowedPrivateNetworksKey"/>.</summary>
    public static IServiceCollection AddPublicNetworkPolicy(this IServiceCollection services)
    {
        services.TryAddSingleton<PublicNetworkPolicy>();
        return services;
    }

    /// <summary>Makes a named client reach public internet addresses only (see <see cref="PublicNetworkPolicy"/>).</summary>
    public static IHttpClientBuilder RestrictToPublicNetworks(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddPublicNetworkPolicy();
        return builder.ConfigurePrimaryHttpMessageHandler(static services => services.GetRequiredService<PublicNetworkPolicy>().CreateHandler());
    }
}
