using Dapper;
using Npgsql;

namespace Quicker.Migrator.Maintenance;

/// <summary>
/// <c>operator --email ADDRESS [--revoke]</c>: makes an existing person a platform operator (or no longer one). Operators
/// see every workspace's background work and act on the outbox; the flag is deliberately not settable through the API,
/// so granting it takes access to the database owner's connection. Takes effect at the person's next sign-in.
/// </summary>
internal static class PlatformOperators
{
    public static async Task<int> RunAsync(string ownerConnection, string? email, bool revoke)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            Console.Error.WriteLine("operator needs --email <address> (add --revoke to take the role away).");
            return 2;
        }

        await using var owner = new NpgsqlConnection(ownerConnection);
        await owner.OpenAsync();
        var changed = await owner.ExecuteAsync(
            "UPDATE control.users SET is_platform_operator = @grant, updated_at = now() WHERE lower(email) = lower(@email)",
            new { grant = !revoke, email = email.Trim() });
        if (changed == 0)
        {
            Console.Error.WriteLine($"No person signs in as '{email}'. They sign up or accept an invitation first.");
            return 1;
        }

        Console.WriteLine(revoke ? $"{email} is no longer a platform operator." : $"{email} is a platform operator from their next sign-in.");
        return 0;
    }
}
