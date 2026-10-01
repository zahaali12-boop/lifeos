using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Cbor;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Identity.Tests;

/// <summary>
/// Security keys end to end against the real verification (Fido2NetLib): a software authenticator makes a P-256 key,
/// answers the registration with a "none" attestation and signs sign-in and step-up challenges, exactly as a hardware
/// key or a platform passkey does over the browser API.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WebAuthnTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    [Fact]
    public async Task A_security_key_is_registered_used_to_sign_in_and_to_step_up()
    {
        var ws = await Api.SignupAsync();
        using var key = new SoftwareAuthenticator("http://localhost");
        using var client = Api.ClientFor(ws.AccessToken);

        var options = await (await client.PostAsync("/api/v1/me/mfa/webauthn/register/options", null)).ReadJsonAsync();
        options.GetProperty("options").GetProperty("rp").GetProperty("id").GetString().ShouldBe("localhost");
        var registered = await client.PostAsJsonAsync("/api/v1/me/mfa/webauthn/register/verify", new { optionsId = options.GetProperty("optionsId").GetGuid(), name = "Desk key", response = key.Register(options.GetProperty("options")) }, Json);
        registered.StatusCode.ShouldBe(HttpStatusCode.OK);
        var method = await registered.ReadJsonAsync();
        method.GetProperty("kind").GetString().ShouldBe("webauthn");
        method.GetProperty("name").GetString().ShouldBe("Desk key");

        // The same registration options cannot be used twice.
        var replayed = await client.PostAsJsonAsync("/api/v1/me/mfa/webauthn/register/verify", new { optionsId = options.GetProperty("optionsId").GetGuid(), name = "Again", response = key.Register(options.GetProperty("options")) }, Json);
        (await replayed.ErrorCodeAsync()).ShouldBe("mfa.options_expired");

        // Sign-in now asks for the key, and the key's signature completes it.
        var login = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = ws.OwnerPassword }, Json)).ReadJsonAsync();
        login.GetProperty("status").GetString().ShouldBe("mfa_required");
        login.GetProperty("mfaMethods").EnumerateArray().Select(static m => m.GetString()).ShouldBe(["webauthn"]);
        var challenge = login.GetProperty("challengeToken").GetString();
        var assertion = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/mfa/webauthn/options", new { challengeToken = challenge }, Json)).ReadJsonAsync();
        var signedIn = await Api.Client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { challengeToken = challenge, webAuthnOptionsId = assertion.GetProperty("optionsId").GetGuid(), webAuthnResponse = key.Sign(assertion.GetProperty("options"), ws.UserId) }, Json);
        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tokens = (await signedIn.ReadJsonAsync()).GetProperty("tokens");
        using var keyClient = Api.ClientFor(tokens.GetProperty("accessToken").GetString()!);
        var sessions = await (await keyClient.GetAsync("/api/v1/me/sessions")).ReadJsonAsync();
        sessions.EnumerateArray().Single(static s => s.GetProperty("isCurrent").GetBoolean()).GetProperty("amr").GetString().ShouldBe("pwd mfa hwk");
        var methods = await (await keyClient.GetAsync("/api/v1/me/mfa")).ReadJsonAsync();
        methods.EnumerateArray().Single().GetProperty("lastUsedAt").ValueKind.ShouldBe(JsonValueKind.String);

        // A sensitive action after the step-up window: the password is not enough for someone with a second factor, the key is.
        Api.Clock.Advance(TimeSpan.FromMinutes(6));
        (await (await keyClient.PostAsJsonAsync("/api/v1/api-keys", new { name = "k" }, Json)).ErrorCodeAsync()).ShouldBe("auth.step_up_required");
        (await (await keyClient.PostAsJsonAsync("/api/v1/me/step-up", new { password = ws.OwnerPassword }, Json)).ErrorCodeAsync()).ShouldBe("auth.step_up_failed");
        var stepUpOptions = await (await keyClient.PostAsync("/api/v1/me/step-up/webauthn/options", null)).ReadJsonAsync();
        var stepUp = await keyClient.PostAsJsonAsync("/api/v1/me/step-up", new { webAuthnOptionsId = stepUpOptions.GetProperty("optionsId").GetGuid(), webAuthnResponse = key.Sign(stepUpOptions.GetProperty("options"), ws.UserId) }, Json);
        stepUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var fresh = Api.ClientFor((await stepUp.ReadJsonAsync()).GetProperty("accessToken").GetString()!);
        (await fresh.PostAsJsonAsync("/api/v1/api-keys", new { name = "k" }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Keys_answering_for_another_site_or_with_a_forged_signature_are_refused()
    {
        var ws = await Api.SignupAsync();
        using var client = Api.ClientFor(ws.AccessToken);

        // A key registered while the browser was on another site (a phishing page relaying the challenge).
        using var phished = new SoftwareAuthenticator("https://quicker.example.evil");
        var options = await (await client.PostAsync("/api/v1/me/mfa/webauthn/register/options", null)).ReadJsonAsync();
        var refused = await client.PostAsJsonAsync("/api/v1/me/mfa/webauthn/register/verify", new { optionsId = options.GetProperty("optionsId").GetGuid(), name = "x", response = phished.Register(options.GetProperty("options")) }, Json);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.ErrorCodeAsync()).ShouldBe("mfa.webauthn_invalid");

        using var key = new SoftwareAuthenticator("http://localhost");
        options = await (await client.PostAsync("/api/v1/me/mfa/webauthn/register/options", null)).ReadJsonAsync();
        (await client.PostAsJsonAsync("/api/v1/me/mfa/webauthn/register/verify", new { optionsId = options.GetProperty("optionsId").GetGuid(), name = "Desk key", response = key.Register(options.GetProperty("options")) }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A signature that does not match the key is refused and the sign-in stays open for a real one.
        var login = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = ws.OwnerPassword }, Json)).ReadJsonAsync();
        var challenge = login.GetProperty("challengeToken").GetString();
        var assertion = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/mfa/webauthn/options", new { challengeToken = challenge }, Json)).ReadJsonAsync();
        var forged = await Api.Client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { challengeToken = challenge, webAuthnOptionsId = assertion.GetProperty("optionsId").GetGuid(), webAuthnResponse = key.Sign(assertion.GetProperty("options"), ws.UserId, forge: true) }, Json);
        forged.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await forged.ErrorCodeAsync()).ShouldBe("auth.mfa_invalid");
        assertion = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/mfa/webauthn/options", new { challengeToken = challenge }, Json)).ReadJsonAsync();
        (await Api.Client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { challengeToken = challenge, webAuthnOptionsId = assertion.GetProperty("optionsId").GetGuid(), webAuthnResponse = key.Sign(assertion.GetProperty("options"), ws.UserId) }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>A P-256 authenticator in memory, speaking the browser API's JSON shapes (binary as base64url).</summary>
    private sealed class SoftwareAuthenticator(string origin) : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly byte[] _credentialId = RandomNumberGenerator.GetBytes(16);
        private uint _counter;

        public object Register(JsonElement options)
        {
            var rpId = options.GetProperty("rp").GetProperty("id").GetString()!;
            var clientData = ClientData("webauthn.create", options.GetProperty("challenge").GetString()!);
            var authData = AuthenticatorData(rpId, attested: true);
            var attestation = new CborWriter(CborConformanceMode.Lax);
            attestation.WriteStartMap(3);
            attestation.WriteTextString("fmt");
            attestation.WriteTextString("none");
            attestation.WriteTextString("attStmt");
            attestation.WriteStartMap(0);
            attestation.WriteEndMap();
            attestation.WriteTextString("authData");
            attestation.WriteByteString(authData);
            attestation.WriteEndMap();
            var id = Base64Url.EncodeToString(_credentialId);
            return new { id, rawId = id, type = "public-key", response = new { attestationObject = Base64Url.EncodeToString(attestation.Encode()), clientDataJSON = Base64Url.EncodeToString(clientData), transports = new[] { "usb" } }, clientExtensionResults = new { } };
        }

        public object Sign(JsonElement options, Guid userId, bool forge = false)
        {
            var rpId = options.TryGetProperty("rpId", out var rp) && rp.ValueKind == JsonValueKind.String ? rp.GetString()! : "localhost";
            var clientData = ClientData("webauthn.get", options.GetProperty("challenge").GetString()!);
            var authData = AuthenticatorData(rpId, attested: false);
            var signature = _key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            if (forge)
            {
                using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                signature = other.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }

            var id = Base64Url.EncodeToString(_credentialId);
            return new
            {
                id,
                rawId = id,
                type = "public-key",
                response = new { authenticatorData = Base64Url.EncodeToString(authData), clientDataJSON = Base64Url.EncodeToString(clientData), signature = Base64Url.EncodeToString(signature), userHandle = Base64Url.EncodeToString(userId.ToByteArray()) },
                clientExtensionResults = new { },
            };
        }

        public void Dispose() => _key.Dispose();

        private byte[] ClientData(string type, string challenge) =>
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin, crossOrigin = false }));

        /// <summary>RP id hash, flags (user present and verified; attested credential data on registration), counter, and on registration the credential and its COSE public key.</summary>
        private byte[] AuthenticatorData(string rpId, bool attested)
        {
            _counter++;
            var data = new List<byte>(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
            data.Add((byte)(0x01 | 0x04 | (attested ? 0x40 : 0)));
            var counter = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(counter, _counter);
            data.AddRange(counter);
            if (attested)
            {
                data.AddRange(new byte[16]); // AAGUID: none
                var length = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)_credentialId.Length);
                data.AddRange(length);
                data.AddRange(_credentialId);
                var point = _key.ExportParameters(false).Q;
                var cose = new CborWriter(CborConformanceMode.Lax);
                cose.WriteStartMap(5);
                cose.WriteInt32(1);
                cose.WriteInt32(2); // kty: EC2
                cose.WriteInt32(3);
                cose.WriteInt32(-7); // alg: ES256
                cose.WriteInt32(-1);
                cose.WriteInt32(1); // crv: P-256
                cose.WriteInt32(-2);
                cose.WriteByteString(point.X!);
                cose.WriteInt32(-3);
                cose.WriteByteString(point.Y!);
                cose.WriteEndMap();
                data.AddRange(cose.Encode());
            }

            return [.. data];
        }
    }
}
