using ArturRios.Cerberus.Query.Sync;
using ArturRios.Cerberus.Query.Profiles;
using ArturRios.Cerberus.Shared.Configuration;

namespace ArturRios.Cerberus.Query.Tests;

public class SyncCursorTests
{
    private static CerberusOptions Options(string? key = null) => new()
    { RegistrationFingerprintKey = key ?? "independent-durable-sync-fixture-key-32-bytes" };
    private static SyncContinuation State() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 0, 25, 10);
    [UnitTheory]
    [InlineData(0, 0)][InlineData(0, 25)][InlineData(10, 25)][InlineData(25, 25)]
    public void GivenDurableProfileCheckpoint_WhenEncoding_ThenRoundTripWithoutAnOnlineHandle(long after, long boundary)
    {
        var s = State() with { After = after, Boundary = boundary };
        var c = new SyncCursor(Options());
        var token = c.Encode(s);
        Assert.True(c.TryDecode(token, out var restored)); Assert.Equal(s, restored);
        Assert.DoesNotContain(s.Actor.ToString("D"), token);
        Assert.DoesNotContain(s.ProfileId.ToString("D"), token);
        Assert.NotEqual(token, c.Encode(s));
        Assert.False(new ProfileListCursor(Options()).TryDecode(token, out _));
    }
    [UnitTheory]
    [InlineData("actor")][InlineData("profile")][InlineData("stream")][InlineData("version")]
    [InlineData("unsafeVersion")][InlineData("after")][InlineData("boundary")][InlineData("unsafeBoundary")]
    [InlineData("skip")][InlineData("page")]
    public void GivenInvalidCheckpoint_WhenEncoding_ThenReject(string invalid)
    {
        var s = State();
        s = invalid switch
        {
            "actor" => s with { Actor = Guid.Empty }, "profile" => s with { ProfileId = Guid.Empty },
            "stream" => s with { StreamId = Guid.Empty }, "version" => s with { WindowVersion = 0 },
            "unsafeVersion" => s with { WindowVersion = 9007199254740992 }, "after" => s with { After = -1 },
            "boundary" => s with { Boundary = -1 }, "unsafeBoundary" => s with { Boundary = 9007199254740992 },
            "skip" => s with { After = 26 }, _ => s with { PageSize = 0 }
        };
        Assert.Throws<ArgumentException>(() => new SyncCursor(Options()).Encode(s));
    }
    [UnitTheory]
    [InlineData("empty")][InlineData("format")][InlineData("padding")][InlineData("oversized")]
    [InlineData("nonce")][InlineData("tag")][InlineData("payload")][InlineData("wrongKey")]
    public void GivenMalformedOrTamperedCursor_WhenDecoding_ThenRequireResynchronization(string invalid)
    {
        var c = new SyncCursor(Options()); var token = c.Encode(State());
        if (invalid == "empty") token = "";
        if (invalid == "format") token = "invalid!";
        if (invalid == "padding") token += "=";
        if (invalid == "oversized") token = new string('A', 2049);
        if (invalid is "nonce" or "tag" or "payload")
        {
            var raw = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + new string('=', (4 - token.Length % 4) % 4));
            raw[invalid == "nonce" ? 0 : invalid == "tag" ? 12 : 28] ^= 1;
            token = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        if (invalid == "wrongKey") c = new SyncCursor(Options("different-durable-sync-fixture-key-32-bytes"));
        Assert.False(c.TryDecode(token, out var result)); Assert.Null(result);
    }
}
