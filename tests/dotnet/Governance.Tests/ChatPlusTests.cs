using GovernanceService.Infrastructure.Chat;
using GovernanceService.Models;
using Xunit;

namespace Governance.Tests;

/// <summary>Dalga 5e: sohbet botunun saf mantığı (BG7 bulanık eşleşme, BG14 hız sınırı, BG6 sessiz saat, BG10 düğme süresi, B6, B7, B14, BG16).</summary>
public class ChatTextTests
{
    [Theory]
    [InlineData("İZİN BAKİYEM", "izin bakiyem")]
    [InlineData("  Çıktım!  ", "ciktim")]
    [InlineData("<at>HR360</at> bakiye", "bakiye")]
    [InlineData("Geçmişimi  sil", "gecmisimi sil")]
    public void Fold_lowercases_and_folds_turkish(string input, string expected) => Assert.Equal(expected, ChatText.Fold(input));

    [Theory]
    [InlineData("bakiye", "bakiye", 0)]
    [InlineData("bakiye", "bakiy", 1)]
    [InlineData("bakyie", "bakiye", 1)] // yer değiştirme tek hata
    [InlineData("onaylarm", "onaylarim", 1)]
    [InlineData("masa", "vardiya", 5)]
    public void Levenshtein_with_transposition(string a, string b, int d) => Assert.Equal(d, ChatText.Levenshtein(a, b));

    [Theory]
    [InlineData("bakiye", "balance", false)]
    [InlineData("Bakiye", "balance", false)]
    [InlineData("bakyie", "balance", true)]
    [InlineData("onaylarm", "approvals", true)]
    [InlineData("izin al", "leave", false)]
    [InlineData("izin iptal", "cancelleave", false)]
    [InlineData("vardiyam", "shifts", false)]
    [InlineData("vardyam", "shifts", true)]
    [InlineData("çıktım", "clockout", false)]
    [InlineData("geldim", "clockin", false)]
    [InlineData("geçmişimi sil", "forget", false)]
    [InlineData("duyrular", "announcements", true)]
    [InlineData("balance", "balance", false)]
    [InlineData("", "", false)]
    public void Resolve_commands_with_typos(string input, string cmd, bool fuzzy)
    {
        var r = ChatCommands.Resolve(input);
        Assert.NotNull(r);
        Assert.Equal(cmd, r!.Cmd);
        Assert.Equal(fuzzy, r.Fuzzy);
    }

    [Fact]
    public void Resolve_keeps_arguments_in_original_case()
    {
        var r = ChatCommands.Resolve("teşekkür <@U_AYSE> Harika iş!");
        Assert.Equal("kudos", r!.Cmd);
        Assert.Equal("<@U_AYSE> Harika iş!", r.Args);
        var m = ChatCommands.Resolve("masa yarın");
        Assert.Equal("desk", m!.Cmd);
        Assert.Equal("yarın", m.Args);
        var t = ChatCommands.Resolve("<at>HR360</at> teşekkür <at>Ayşe Yılmaz</at> süper");
        Assert.Equal("kudos", t!.Cmd);
        Assert.Contains("<at>Ayşe Yılmaz</at>", t.Args);
    }

    [Theory]
    [InlineData("sonraki resmi tatil ne zaman?")]
    [InlineData("son 6 ayda departmanlara göre izin günleri")]
    [InlineData("izin bakiyem ne kadar kaldı acaba")]
    [InlineData("saçma")]
    public void Natural_language_goes_to_the_assistant(string input) => Assert.Null(ChatCommands.Resolve(input));

    [Fact]
    public void Feature_catalog_maps_commands()
    {
        Assert.Equal("shifts", ChatFeatureCatalog.FeatureOf("swaps"));
        Assert.Equal("clock", ChatFeatureCatalog.FeatureOf("clockin"));
        Assert.Null(ChatFeatureCatalog.FeatureOf("help"));
        Assert.All(ChatCommands.Keywords.Values.Select(v => ChatFeatureCatalog.FeatureOf(v.Cmd)).Where(f => f is not null), f => Assert.Contains(f!, ChatFeatureCatalog.Keys));
    }
}

public class ChatLimiterTests
{
    [Fact]
    public void Sliding_window_blocks_then_recovers()
    {
        var l = new SlidingWindowLimiter();
        var t0 = new DateTime(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);
        var w = TimeSpan.FromSeconds(60);
        for (var i = 0; i < 3; i++) Assert.True(l.Hit("u", 3, w, t0.AddSeconds(i)).Allowed);
        var (ok, retry) = l.Hit("u", 3, w, t0.AddSeconds(10));
        Assert.False(ok);
        Assert.Equal(TimeSpan.FromSeconds(50), retry);
        Assert.True(l.Hit("other", 3, w, t0.AddSeconds(10)).Allowed); // anahtarlar bağımsız
        Assert.True(l.Hit("u", 3, w, t0.AddSeconds(61)).Allowed);     // ilk istek pencereden çıktı
        l.Sweep(w, t0.AddMinutes(10));
        Assert.True(l.Hit("u", 1, w, t0.AddMinutes(10)).Allowed);
    }
}

public class ChatQuietHoursTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Critical_is_always_sent() =>
        Assert.Equal(QuietHours.Action.Send, QuietHours.Decide(true, false, Now.AddHours(5), Now).Action);

    [Fact]
    public void Chat_channel_off_skips() =>
        Assert.Equal(QuietHours.Action.Skip, QuietHours.Decide(false, false, null, Now).Action);

    [Fact]
    public void Quiet_hours_defer_until_end()
    {
        var (a, until) = QuietHours.Decide(false, true, Now.AddHours(8), Now);
        Assert.Equal(QuietHours.Action.Defer, a);
        Assert.Equal(Now.AddHours(8), until);
    }

    [Fact]
    public void Past_defer_time_sends() =>
        Assert.Equal(QuietHours.Action.Send, QuietHours.Decide(false, true, Now.AddMinutes(-1), Now).Action);
}

public class ChatButtonExpiryTests
{
    [Fact]
    public void Stamp_round_trips_and_expires()
    {
        var issued = new DateTimeOffset(2026, 10, 1, 12, 30, 0, TimeSpan.Zero);
        var s = ButtonStamp.Encode("abc|1", issued);
        var (v, at) = ButtonStamp.Decode(s);
        Assert.Equal("abc|1", v);
        Assert.Equal(issued, at);
        Assert.False(ButtonStamp.Expired(at, issued.AddDays(6), 7));
        Assert.True(ButtonStamp.Expired(at, issued.AddDays(8), 7));
    }

    [Fact]
    public void Unstamped_values_are_not_expired()
    {
        var (v, at) = ButtonStamp.Decode("plain");
        Assert.Equal("plain", v);
        Assert.Null(at);
        Assert.False(ButtonStamp.Expired(at, DateTimeOffset.UtcNow, 7));
    }
}

public class ChatReceiptTests
{
    [Fact]
    public void Parses_amount_date_and_category()
    {
        var (amount, date, vkn) = ReceiptParser.Parse("LEZZET RESTORAN\nVKN 1234567890\nTARIH 01.10.2026\nKDV 18,00\nTOPLAM 245,50");
        Assert.Equal(245.50m, amount);
        Assert.Equal(new DateOnly(2026, 10, 1), date);
        Assert.Equal("1234567890", vkn);
        Assert.Equal("Meal", ReceiptParser.GuessCategory("LEZZET RESTORAN"));
        Assert.Equal("Transport", ReceiptParser.GuessCategory("OPET AKARYAKIT"));
        Assert.Equal("Other", ReceiptParser.GuessCategory("XYZ"));
    }

    [Theory]
    [InlineData("1.234,50", 1234.50)]
    [InlineData("245.5", 245.5)]
    [InlineData("99 TL", 99)]
    public void Parses_amount_text(string s, double expected) => Assert.Equal((decimal)expected, ReceiptParser.ParseAmount(s));

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("2000000")]
    public void Rejects_invalid_amounts(string s) => Assert.Null(ReceiptParser.ParseAmount(s));
}

public class ChatFollowUpTests
{
    [Fact]
    public void Merges_period_follow_up()
    {
        var m = FollowUp.Merge("Son 6 ayda departmanlara göre izin günleri", "peki geçen ay?");
        Assert.Equal("departmanlara gore izin gunleri gecen ay", m);
    }

    [Fact]
    public void New_question_is_not_a_follow_up()
    {
        Assert.Null(FollowUp.Merge("izin günleri", "sonraki resmi tatil ne zaman"));
        Assert.Null(FollowUp.Merge(null, "peki geçen ay?"));
    }
}

public class ChatHostTests
{
    [Theory]
    [InlineData("Mattermost", "https://acme.cloud.mattermost.com", "mattermost")]
    [InlineData("Mattermost", "https://chat.acme.com.tr", null)]
    [InlineData("RocketChat", "https://acme.rocket.chat", "rocketchat")]
    [InlineData("RocketChat", "http://rocket.internal:3000", null)]
    [InlineData("RocketChat", "https://evilrocket.chat.example.com", null)]
    public void Saas_detection(string platform, string url, string? key) => Assert.Equal(key, ChatHosts.SaasKey(platform, url));

    [Fact]
    public void Self_hosted_needs_no_transfer_agreement()
    {
        var none = new HashSet<string>();
        Assert.True(ChatHosts.Allowed(new ChatApp { Platform = "Mattermost", ServerUrl = "https://chat.acme.local" }, none));
        Assert.False(ChatHosts.Allowed(new ChatApp { Platform = "Mattermost", ServerUrl = "https://x.cloud.mattermost.com" }, none));
        Assert.False(ChatHosts.Allowed(new ChatApp { Platform = "Slack" }, none));
        Assert.True(ChatHosts.Allowed(new ChatApp { Platform = "Slack" }, new HashSet<string> { "slack" }));
    }

    [Fact]
    public void Card_action_parsing_and_signing()
    {
        Assert.Equal("desk_book", ChatCards.SlackAction("hr360x:desk_book:3"));
        Assert.Null(ChatCards.SlackAction("hr360_approve"));
        Assert.Equal(("pulse", "id|3~abc"), ChatCards.RocketAction("hr360x pulse id|3~abc"));
        Assert.Null(ChatCards.RocketAction("bakiye"));
        var sig = IncomingToken.Sign("secret", "a", "v");
        Assert.True(IncomingToken.Matches(sig, IncomingToken.Sign("secret", "a", "v")));
        Assert.False(IncomingToken.Matches(sig, IncomingToken.Sign("other", "a", "v")));
        Assert.False(IncomingToken.Matches("", ""));
    }
}
