using System.Security.Cryptography;
using System.Text;
using Quicker.Kernel.Results;
using Quicker.Tenancy.Contracts;

namespace Quicker.Identity.Application;

/// <summary>Checks a candidate password against a breach corpus. The HIBP adapter uses k-anonymity (5-char prefix).</summary>
public interface IBreachedPasswordChecker
{
    Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken);
}

public sealed class NoBreachCheck : IBreachedPasswordChecker
{
    public Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken) => Task.FromResult(false);
}

public sealed class HibpBreachedPasswordChecker(HttpClient http) : IBreachedPasswordChecker
{
    public async Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken)
    {
#pragma warning disable CA5350 // The HIBP range API is defined over SHA-1 prefixes; nothing secret is derived from it.
        var sha1 = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
#pragma warning restore CA5350
        var prefix = sha1[..5];
        var suffix = sha1[5..];
        try
        {
            using var response = await http.GetAsync(new Uri($"https://api.pwnedpasswords.com/range/{prefix}"), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false; // fail open: availability of a third party must not block password changes
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            foreach (var line in body.Split('\n'))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && line.AsSpan(0, colon).Trim().Equals(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}

public sealed class PasswordPolicy(IBreachedPasswordChecker breachChecker)
{
    public async Task<Result> ValidateAsync(string password, string email, TenantSecurityPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrEmpty(password) || password.Length < policy.PasswordMinLength)
        {
            return Error.Validation("password.too_short", $"Password must be at least {policy.PasswordMinLength} characters.")
                .WithWhy(("minLength", policy.PasswordMinLength));
        }

        if (password.Length > 256)
        {
            return Error.Validation("password.too_long", "Password must be at most 256 characters.");
        }

        var local = email.Split('@')[0];
        if (local.Length >= 4 && password.Contains(local, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("password.contains_email", "Password must not contain your email address.");
        }

        if (password.Distinct().Count() < 4)
        {
            return Error.Validation("password.too_simple", "Password uses too few distinct characters.");
        }

        if (await breachChecker.IsBreachedAsync(password, cancellationToken))
        {
            return Error.Validation("password.breached", "This password appears in known data breaches; choose another.");
        }

        return Result.Success();
    }
}
