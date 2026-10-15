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
    public void Records_without_reasoning_keep_the_hashes_files_were_saved_with()
    {
        // Hashes stored by 1.0 (copies, judgments, analyses) stay valid (ADR-052).
        Assert.Equal(
            "0be9529c04c53e83f5553f4f5fdc906a254951a01192f7855ce84c6c772f568d",
            new InstructionPayload("Summarize", "text", "summary", reasoning: "  ").ComputeContentHash());
        Assert.Equal(
            "48a1a1e5c707fef3103da9574dfa70ed55081110d5b25dfa243400512f3213fc",
            new ChatPayload(null, [new(ChatRole.User, "hi"), new(ChatRole.Assistant, "hello")]).ComputeContentHash());
    }

    [Fact]
    public void Reasoning_is_part_of_the_content()
    {
        var plain = new InstructionPayload("2+2?", "", "4");
        var reasoned = new InstructionPayload("2+2?", "", "4", "Two and two make four.");
        var chat = new ChatPayload(null, [new(ChatRole.User, "2+2?"), new(ChatRole.Assistant, "4", "Two and two make four.")]);
        var otherThought = new ChatPayload(null, [new(ChatRole.User, "2+2?"), new(ChatRole.Assistant, "4", "Count them.")]);

        Assert.NotEqual(plain.ComputeContentHash(), reasoned.ComputeContentHash());
        Assert.NotEqual(chat.ComputeContentHash(), otherThought.ComputeContentHash());
        Assert.NotEqual(chat.ComputeContentHash(), new ChatPayload(null, [new(ChatRole.User, "2+2?"), new(ChatRole.Assistant, "4")]).ComputeContentHash());

        // The reasoning cannot be confused with the next turn.
        var thoughtLikeTurn = new ChatPayload(null, [new(ChatRole.Assistant, "a", "User"), new(ChatRole.User, "b")]);
        var twoTurns = new ChatPayload(null, [new(ChatRole.Assistant, "a"), new(ChatRole.User, "User"), new(ChatRole.User, "b")]);
        Assert.NotEqual(thoughtLikeTurn.ComputeContentHash(), twoTurns.ComputeContentHash());
    }

    [Fact]
    public void Hash_does_not_normalize_whitespace()
    {
        var first = new InstructionPayload("Summarize", "", "x");
        var second = new InstructionPayload("Summarize ", "", "x");

        Assert.NotEqual(first.ComputeContentHash(), second.ComputeContentHash());
    }
}
