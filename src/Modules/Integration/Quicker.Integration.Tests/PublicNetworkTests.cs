using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Web;

namespace Quicker.Integration.Tests;

/// <summary>Addresses a tenant chooses stay on the public internet: when saved, and when the connection is opened.</summary>
[Collection(ApiCollection.Name)]
public sealed class PublicNetworkTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.20.30.40", false)]
    [InlineData("172.16.5.4", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:169.254.169.254", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2002:a00:1::1", false)]
    public void Only_public_internet_addresses_are_allowed(string address, bool allowed) =>
        new PublicNetworkPolicy([]).Allows(IPAddress.Parse(address)).ShouldBe(allowed);

    [Fact]
    public void Operators_can_allow_named_internal_networks_and_nothing_else()
    {
        var policy = new PublicNetworkPolicy(PublicNetworkPolicy.Parse("10.1.0.0/16, 192.168.7.9"));
        policy.Allows(IPAddress.Parse("10.1.200.3")).ShouldBeTrue();
        policy.Allows(IPAddress.Parse("::ffff:10.1.200.3")).ShouldBeTrue();
        policy.Allows(IPAddress.Parse("192.168.7.9")).ShouldBeTrue();
        policy.Allows(IPAddress.Parse("10.2.0.1")).ShouldBeFalse();
        policy.Allows(IPAddress.Parse("192.168.7.10")).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => PublicNetworkPolicy.Parse("10.0.0.0/8, intranet"));
    }

    [Fact]
    public async Task The_connection_is_refused_on_the_address_resolved_so_a_name_cannot_lead_to_a_private_host()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // "localhost" is a name, not an address: nothing at save time would spot it; the connection does.
        using (var guarded = new HttpClient(new PublicNetworkPolicy([]).CreateHandler()))
        {
            var refused = await Should.ThrowAsync<HttpRequestException>(() => guarded.GetAsync(new Uri($"http://localhost:{port}/")));
            refused.InnerException.ShouldBeOfType<NonPublicDestinationException>().Host.ShouldBe("localhost");
            await Should.ThrowAsync<HttpRequestException>(() => guarded.GetAsync(new Uri($"http://127.0.0.1:{port}/")));
        }

        // With loopback allowed by the operator the same request connects.
        using var allowed = new HttpClient(new PublicNetworkPolicy(PublicNetworkPolicy.Parse("127.0.0.0/8")).CreateHandler()) { Timeout = TimeSpan.FromSeconds(10) };
        var pending = allowed.GetAsync(new Uri($"http://127.0.0.1:{port}/"));
        using var accepted = await listener.AcceptTcpClientAsync();
        var stream = accepted.GetStream();
        var buffer = new byte[1024];
        _ = await stream.ReadAsync(buffer);
        await stream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
        (await pending).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_webhook_to_a_private_address_is_refused_when_saved_and_not_delivered_if_it_gets_there()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);

        foreach (var url in new[] { "http://10.0.0.5/hook", "http://169.254.169.254/latest/meta-data/", "http://[::1]:8080/", "http://[fd00::7]/", "http://192.168.1.20:9000/in" })
        {
            var refused = await owner.PostAsJsonAsync("/api/v1/integration/webhooks", new { name = "internal", url, eventTypes = new[] { "*" } }, Json);
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, url);
            (await refused.ErrorCodeAsync()).ShouldBe("webhook.url_blocked", url);
        }

        // A subscription whose address became private after it was saved (edited in the database here, as a DNS change
        // would) fails at once, without a day of retries, and nothing reaches the address.
        var created = await (await owner.PostAsJsonAsync("/api/v1/integration/webhooks", new { name = "moved", url = host.Receiver.BaseUrl + "moved", eventTypes = new[] { "*" } }, Json)).ReadJsonAsync();
        var id = created.GetProperty("subscription").GetProperty("id").GetGuid();
        var port = new Uri(host.Receiver.BaseUrl).Port;
        await using (var db = new NpgsqlConnection(Api.Db.OwnerConnectionString))
        {
            await db.ExecuteAsync("UPDATE app.int_webhook_subscriptions SET url = @url WHERE id = @id", new { url = $"http://[::1]:{port}/moved", id });
        }

        (await owner.PostAsync($"/api/v1/integration/webhooks/{id}/test", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await host.RunWorkerAsync();
        var delivery = (await (await owner.GetAsync($"/api/v1/integration/webhooks/{id}/deliveries")).ReadJsonAsync()).GetProperty("items").EnumerateArray().Single();
        delivery.GetProperty("status").GetString().ShouldBe("failed");
        delivery.GetProperty("attempt").GetInt32().ShouldBe(1);
        delivery.GetProperty("lastError").GetString()!.ShouldContain("not a public internet address");
        delivery.GetProperty("nextAttemptAt").ValueKind.ShouldBe(JsonValueKind.Null);
        host.Receiver.Received.ShouldNotContain(r => r.Path == "/moved");
    }
}
