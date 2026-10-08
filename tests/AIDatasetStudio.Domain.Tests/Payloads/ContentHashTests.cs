using AIDatasetStudio.Domain.Enums;
using AIDatasetStudio.Domain.Payloads;

namespace AIDatasetStudio.Domain.Tests.Payloads;

public sealed class ContentHashTests
{
    [Fact]
    public void Same_content_produces_same_hash()
    {
        var first = new InstructionPayload("Summarize", "text", "summary");
        var second = new InstructionPayload("Summarize", "text", "summary");

        Assert.Equal(first.ComputeContentHash(), second.ComputeContentHash());
    }

    [Fact]
    public void Hash_is_lowercase_sha256_hex()
    {
        var hash = new InstructionPayload("a", "b", "c").ComputeContentHash();

        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Moving_text_between_fields_changes_the_hash()
    {
        var first = new InstructionPayload("ab", "c", "");
        var second = new InstructionPayload("a", "bc", "");

        Assert.NotEqual(first.ComputeContentHash(), second.ComputeContentHash());
    }

    [Fact]
    public void Same_text_in_different_payload_types_hashes_differently()
    {
        var instruction = new InstructionPayload("p", "c", "r");
        var preference = new PreferencePayload("p", "c", "r");

        Assert.NotEqual(instruction.ComputeContentHash(), preference.ComputeContentHash());
    }

    [Fact]
    public void Chat_hash_depends_on_roles_and_system_prompt()
    {
        var baseline = new ChatPayload(null, [new(ChatRole.User, "hi"), new(ChatRole.Assistant, "hello")]);
        var swappedRoles = new ChatPayload(null, [new(ChatRole.Assistant, "hi"), new(ChatRole.User, "hello")]);
        var withSystem = new ChatPayload("be brief", [new(ChatRole.User, "hi"), new(ChatRole.Assistant, "hello")]);

        Assert.NotEqual(baseline.ComputeContentHash(), swappedRoles.ComputeContentHash());
        Assert.NotEqual(baseline.ComputeContentHash(), withSystem.ComputeContentHash());
    }

    [Fact]
    public void Hash_does_not_normalize_whitespace()
    {
        var first = new InstructionPayload("Summarize", "", "x");
        var second = new InstructionPayload("Summarize ", "", "x");

        Assert.NotEqual(first.ComputeContentHash(), second.ComputeContentHash());
    }
}
