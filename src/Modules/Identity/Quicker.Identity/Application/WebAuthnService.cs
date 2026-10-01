using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Quicker.Identity.Domain;
using Quicker.Identity.Persistence;
using Quicker.Identity.Security;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;

namespace Quicker.Identity.Application;

/// <summary>Passkeys / security keys as an MFA factor (registration and assertion), options kept in one-time tokens.</summary>
public sealed class WebAuthnService(IdentityDbContext db, IFido2 fido2, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan OptionsLifetime = TimeSpan.FromMinutes(5);

    public async Task<WebAuthnRegisterOptionsResponse> RegistrationOptionsAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var existing = user.MfaMethods.Where(static m => m.Kind == "webauthn" && m.CredentialId is not null)
            .Select(static m => new PublicKeyCredentialDescriptor(m.CredentialId!)).ToList();

        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = user.Id.ToByteArray(), Name = user.Email, DisplayName = user.DisplayName },
            ExcludeCredentials = existing,
            AuthenticatorSelection = new AuthenticatorSelection { ResidentKey = ResidentKeyRequirement.Preferred, UserVerification = UserVerificationRequirement.Preferred },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var token = await StoreOptionsAsync(user.Id, "webauthn_register", options.ToJson(), cancellationToken);
        return new WebAuthnRegisterOptionsResponse(token, JsonDocument.Parse(options.ToJson()).RootElement);
    }

    public async Task<Result<MfaMethodSummary>> CompleteRegistrationAsync(User user, WebAuthnRegisterVerifyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        var optionsJson = await ConsumeOptionsAsync(user.Id, request.OptionsId, "webauthn_register", cancellationToken);
        if (optionsJson is null)
        {
            return Error.Validation("mfa.options_expired", "Start the registration again.");
        }

        var options = JsonSerializer.Deserialize<CredentialCreateOptions>(optionsJson, Json)!;
        var response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(request.Response.GetRawText(), Json);
        if (response is null)
        {
            return Error.Validation("mfa.webauthn_invalid", "The authenticator response is malformed.");
        }

        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = async (p, ct) => !await db.MfaMethods.AnyAsync(m => m.CredentialId == p.CredentialId, ct),
            }, cancellationToken);
        }
        catch (Fido2VerificationException ex)
        {
            return Error.Validation("mfa.webauthn_invalid", ex.Message);
        }

        var method = new MfaMethod
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            Kind = "webauthn",
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Security key" : request.Name.Trim(),
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            Aaguid = credential.AaGuid,
            Transports = credential.Transports?.Select(static t => t.ToString().ToLowerInvariant()).ToArray(),
            VerifiedAt = clock.UtcNow,
            CreatedAt = clock.UtcNow,
        };
        db.MfaMethods.Add(method);
        await db.SaveChangesAsync(cancellationToken);
        return new MfaMethodSummary(method.Id, method.Kind, method.Name, method.VerifiedAt, method.LastUsedAt);
    }

    public async Task<Result<WebAuthnAssertionOptionsResponse>> AssertionOptionsAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var allowed = user.MfaMethods.Where(static m => m.Kind == "webauthn" && m.CredentialId is not null && m.VerifiedAt is not null)
            .Select(static m => new PublicKeyCredentialDescriptor(m.CredentialId!)).ToList();
        if (allowed.Count == 0)
        {
            return Error.Validation("mfa.no_webauthn", "No security key is registered for this account.");
        }

        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams { AllowedCredentials = allowed, UserVerification = UserVerificationRequirement.Preferred });
        var token = await StoreOptionsAsync(user.Id, "webauthn_assert", options.ToJson(), cancellationToken);
        return new WebAuthnAssertionOptionsResponse(token, JsonDocument.Parse(options.ToJson()).RootElement);
    }

    public async Task<Result> VerifyAssertionAsync(User user, Guid optionsId, JsonElement assertion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var optionsJson = await ConsumeOptionsAsync(user.Id, optionsId, "webauthn_assert", cancellationToken);
        if (optionsJson is null)
        {
            return Error.Validation("mfa.options_expired", "Start the sign-in again.");
        }

        var options = JsonSerializer.Deserialize<AssertionOptions>(optionsJson, Json)!;
        var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertion.GetRawText(), Json);
        if (response is null)
        {
            return Error.Validation("mfa.webauthn_invalid", "The authenticator response is malformed.");
        }

        var method = user.MfaMethods.FirstOrDefault(m => m.Kind == "webauthn" && m.CredentialId is not null && m.CredentialId.AsSpan().SequenceEqual(response.RawId));
        if (method is null)
        {
            return Error.Validation("auth.mfa_invalid", "Unknown credential.");
        }

        try
        {
            var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = options,
                StoredPublicKey = method.PublicKey!,
                StoredSignatureCounter = (uint)method.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (p, _) => Task.FromResult(new Guid(p.UserHandle) == user.Id),
            }, cancellationToken);
            method.SignCount = result.SignCount;
        }
        catch (Fido2VerificationException ex)
        {
            return Error.Validation("auth.mfa_invalid", ex.Message);
        }

        method.LastUsedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Guid> StoreOptionsAsync(Guid userId, string purpose, string optionsJson, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        db.OneTimeTokens.Add(new OneTimeToken
        {
            Id = id,
            Kind = "mfa_challenge",
            UserId = userId,
            TokenHash = Tokens.Hash(purpose + ":" + id),
            Payload = JsonSerializer.Serialize(new { purpose, options = JsonDocument.Parse(optionsJson).RootElement }, Json),
            ExpiresAt = clock.UtcNow.Add(OptionsLifetime),
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        return id;
    }

    private async Task<string?> ConsumeOptionsAsync(Guid userId, Guid optionsId, string purpose, CancellationToken cancellationToken)
    {
        var token = await db.OneTimeTokens.SingleOrDefaultAsync(t => t.Id == optionsId && t.UserId == userId && t.Kind == "mfa_challenge", cancellationToken);
        if (token is null || !token.IsUsable(clock.UtcNow))
        {
            return null;
        }

        using var payload = JsonDocument.Parse(token.Payload);
        if (payload.RootElement.GetProperty("purpose").GetString() != purpose)
        {
            return null;
        }

        token.ConsumedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return payload.RootElement.GetProperty("options").GetRawText();
    }

    internal static string Describe(byte[] credentialId) => Encoding.UTF8.GetString(credentialId.Take(8).ToArray());
}
