using System.Text.Json.Nodes;
using Quicker.Audit.Application;

namespace Quicker.Audit.Tests;

public sealed class AuditJsonTests
{
    [Theory]
    [InlineData("password", true)]
    [InlineData("passwordHash", true)]
    [InlineData("password_hash", true)]
    [InlineData("clientSecret", true)]
    [InlineData("clientSecretEnc", true)]
    [InlineData("apiKey", true)]
    [InlineData("refreshToken", true)]
    [InlineData("keyHash", true)]
    [InlineData("recoveryCodes", true)]
    [InlineData("permissionKey", false)]
    [InlineData("code", false)]
    [InlineData("name", false)]
    [InlineData("displayName", false)]
    [InlineData("grants", false)]
    public void Sensitive_names_are_recognised(string name, bool sensitive) => AuditJson.IsSensitive(name).ShouldBe(sensitive);

    [Fact]
    public void Redaction_replaces_secret_values_at_any_depth_and_keeps_the_rest()
    {
        var node = AuditJson.ToNode(new { name = "k", keyHash = "abc", nested = new { clientSecret = "s", scopes = new[] { "a" } }, list = new[] { new { token = "t", label = "ok" } } });
        var redacted = (JsonObject)AuditJson.Redact(node)!;
        redacted["name"]!.GetValue<string>().ShouldBe("k");
        redacted["keyHash"]!.GetValue<string>().ShouldBe("[redacted]");
        redacted["nested"]!["clientSecret"]!.GetValue<string>().ShouldBe("[redacted]");
        redacted["nested"]!["scopes"]![0]!.GetValue<string>().ShouldBe("a");
        redacted["list"]![0]!["token"]!.GetValue<string>().ShouldBe("[redacted]");
        redacted["list"]![0]!["label"]!.GetValue<string>().ShouldBe("ok");
    }

    [Fact]
    public void Diff_lists_changed_top_level_fields_with_old_and_new()
    {
        var diff = AuditJson.Diff(AuditJson.ToNode(new { a = 1, b = "x", c = new[] { 1, 2 }, d = "same" }), AuditJson.ToNode(new { a = 2, b = (string?)null, c = new[] { 1, 2, 3 }, d = "same", e = true }));
        diff.ShouldNotBeNull();
        diff.Select(static p => p.Key).ShouldBe(["a", "b", "c", "e"], ignoreOrder: true);
        diff["a"]!["old"]!.GetValue<int>().ShouldBe(1);
        diff["a"]!["new"]!.GetValue<int>().ShouldBe(2);
        diff["b"]!["new"].ShouldBeNull();
        diff["e"]!["old"].ShouldBeNull();
        AuditJson.Diff(AuditJson.ToNode(new { a = 1 }), AuditJson.ToNode(new { a = 1 })).ShouldBeNull();
        AuditJson.Diff(null, AuditJson.ToNode(new { a = 1 })).ShouldBeNull();
    }

    [Fact]
    public void Link_hash_is_sha256_of_previous_hash_and_canonical_text()
    {
        var first = ChainVerifier.Link([], "quicker-audit-v1\n1:a");
        first.Length.ShouldBe(32);
        var second = ChainVerifier.Link(first, "quicker-audit-v1\n1:b");
        second.ShouldNotBe(first);
        ChainVerifier.Link(first, "quicker-audit-v1\n1:b").ShouldBe(second);
    }
}
