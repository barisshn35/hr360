using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GovernanceService.Infrastructure.Chat;

/* ======================================================================
 * Dalga 5e — sohbet botunun saf (veritabanına dokunmayan) mantığı. Hepsi
 * birim testlidir (tests/dotnet/Governance.Tests/ChatPlusTests.cs).
 * ==================================================================== */

/// <summary>BG7: Türkçe karakter katlama, Levenshtein uzaklığı ve yazım hatasına dayanıklı komut çözümleme.</summary>
public static class ChatText
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Küçük harf + Türkçe karakter katlama (ı→i, ğ→g...) + noktalama temizliği; boşluklar korunur.</summary>
    public static string Fold(string? s)
    {
        s = StripBotMention(s).ToLower(Tr);
        // Teams'te kişi anmaları "<at>Ad Soyad</at>" biçimindedir: etiket kalkar, ad kalır.
        s = Regex.Replace(s, "</?at>", " ");
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                'ı' => 'i', 'ğ' => 'g', 'ü' => 'u', 'ş' => 's', 'ö' => 'o', 'ç' => 'c', 'â' => 'a', 'î' => 'i', 'û' => 'u',
                _ => char.IsLetterOrDigit(c) ? c : ' ',
            });
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>Teams'te mesajın başındaki bot anması ("&lt;at&gt;HR360&lt;/at&gt; bakiye").</summary>
    public static string StripBotMention(string? s) => Regex.Replace((s ?? "").Trim(), @"^(<at>[^<]*</at>\s*)+", "").Trim();

    public static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        // Optimal hizalama (Damerau-Levenshtein): yer değiştirmiş iki harf ("bakyie" → "bakiye") tek hata sayılır.
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    /// <summary>Kelime uzunluğuna göre izin verilen hata: 1–3 harf 0, 4–7 harf 1, 8+ harf 2.</summary>
    public static int Tolerance(int length) => length <= 3 ? 0 : length <= 7 ? 1 : 2;
}

/// <summary>Çözümlenmiş komut: iç anahtar, İngilizce yazılmış mı, kalan bağımsız değişkenler (özgün metin), bulanık mı eşleşti.</summary>
public sealed record ResolvedCommand(string Cmd, bool English, string Args, bool Fuzzy = false);

public static class ChatCommands
{
    /// <summary>Komut sözlüğü (katlanmış, boşluksuz anahtar → komut, İngilizce mi).</summary>
    public static readonly Dictionary<string, (string Cmd, bool En)> Keywords = Build();

    /// <summary>Bağımsız değişken alan komutlar (ilk kelime eşleşince kalan metin değişkendir).</summary>
    public static readonly HashSet<string> TakesArgs = new() { "kudos", "desk", "anniversary", "code", "expense", "documents", "cancelleave", "swaps" };

    private static Dictionary<string, (string, bool)> Build()
    {
        var d = new Dictionary<string, (string, bool)>();
        void Add(string cmd, bool en, params string[] words) { foreach (var w in words) d[w] = (cmd, en); }
        Add("help", false, "yardim", "komutlar", "menu");
        Add("help", true, "help", "commands");
        Add("approvals", false, "onaylarim", "onay", "onaylar", "onaylarimi");
        Add("approvals", true, "approvals", "myapprovals", "approve");
        Add("balance", false, "bakiye", "izinbakiyem", "izin", "bakiyem");
        Add("balance", true, "balance", "leavebalance", "mybalance");
        Add("leave", false, "izinal", "izintalebi", "yeniizin", "izinistiyorum");
        Add("leave", true, "requestleave", "leaverequest", "newleave", "timeoff");
        Add("home", false, "ozet", "anasayfa", "durum");
        Add("home", true, "summary", "home", "status");
        Add("onleave", false, "izindekiler", "izinde", "kimizinde");
        Add("onleave", true, "onleave", "whoisoff", "whosoff", "whoisonleave");
        Add("whereabouts", false, "kimnerede");
        Add("whereabouts", true, "whereabouts", "whoiswhere", "whoswhere");
        Add("pending", false, "bekleyen");
        Add("pending", true, "pending");
        Add("me", false, "ben", "kimim", "bagla");
        Add("me", true, "me", "whoami", "link");
        // Dalga 5e
        Add("cancelleave", false, "iziniptal", "izniptal", "izinimiiptalet", "izinimiiptal", "izinleriniptal", "izingerial");
        Add("cancelleave", true, "cancelleave", "cancelmyleave");
        Add("expense", false, "masraf", "fis", "masrafekle", "fisekle", "fatura");
        Add("expense", true, "expense", "receipt", "addexpense");
        Add("kudos", false, "tesekkur", "tesekkurler", "takdir", "tesekkurederim");
        Add("kudos", true, "kudos", "thanks", "thank", "thankyou");
        Add("desk", false, "masa", "masaayir", "masalar", "bosmasa", "masarezervasyonu");
        Add("desk", true, "desk", "desks", "bookdesk");
        Add("shifts", false, "vardiyam", "vardiya", "vardiyalarim", "vardiyalar");
        Add("shifts", true, "myshifts", "shifts", "shift");
        Add("swaps", false, "takas", "takaslar", "vardiyatakasi", "takaslarim");
        Add("swaps", true, "swap", "swaps", "shiftswaps");
        Add("announcements", false, "duyurular", "duyuru");
        Add("announcements", true, "announcements", "news");
        Add("documents", false, "belge", "belgetalebi", "belgeler", "belgeiste");
        Add("documents", true, "document", "documents", "certificate");
        Add("clockin", false, "geldim", "girisyap", "mesaibasladi", "isebasladim");
        Add("clockin", true, "clockin", "checkin", "imin");
        Add("clockout", false, "ciktim", "cikisyap", "mesaibitti", "gidiyorum");
        Add("clockout", true, "clockout", "checkout", "imout");
        Add("payslip", false, "bordrom", "bordro", "maasim", "bordropusulam");
        Add("payslip", true, "payslip", "mypayslip");
        Add("hrcase", false, "ikvakasi", "vaka", "ikyesor", "vakaac");
        Add("hrcase", true, "hrcase", "askhr");
        Add("forget", false, "gecmisimisil", "gecmisisil", "gecmisimiunut", "konusmamisil");
        Add("forget", true, "forgetme", "clearhistory", "deletehistory", "deletemyhistory");
        Add("anniversary", false, "yildonumu", "yildonumum");
        Add("anniversary", true, "anniversary");
        Add("code", false, "kod", "dogrulamakodu");
        Add("code", true, "code");
        return d;
    }

    /// <summary>
    /// Metni komuta çözer. Önce tüm metin (boşluksuz) tam eşleşir; sonra ilk bir/iki kelime (bağımsız
    /// değişkenli komutlar ya da en çok 3 kelimelik kısa girdiler için); en son yazım hatası toleransıyla.
    /// Hiçbiri tutmazsa Cmd = "" değil, null döner (doğal dil sorusu → asistan).
    /// </summary>
    public static ResolvedCommand? Resolve(string? raw)
    {
        var folded = ChatText.Fold(raw);
        if (folded.Length == 0) return new("", false, "");
        var whole = folded.Replace(" ", "");
        if (Keywords.TryGetValue(whole, out var exact)) return new(exact.Cmd, exact.En, "");

        var words = folded.Split(' ');
        var rawWords = ChatText.StripBotMention(raw).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string ArgsAfter(int n) => string.Join(' ', rawWords.Skip(n));
        var shortInput = words.Length <= 3;

        // İlk iki kelime ("izin iptal 12.10", "masa ayır yarın").
        for (var n = Math.Min(2, words.Length); n >= 1; n--)
        {
            var head = string.Concat(words.Take(n));
            if (Keywords.TryGetValue(head, out var k) && (TakesArgs.Contains(k.Cmd) || (shortInput && words.Length == n)))
                return new(k.Cmd, k.En, ArgsAfter(n));
        }
        // Yazım hatası toleransı: yalnızca kısa girdilerde ya da değişkenli komutlarda.
        for (var n = Math.Min(2, words.Length); n >= 1; n--)
        {
            var head = string.Concat(words.Take(n));
            var tol = ChatText.Tolerance(head.Length);
            if (tol == 0) continue;
            var best = Keywords.Select(kv => (kv, dist: ChatText.Levenshtein(head, kv.Key)))
                .Where(x => x.dist <= tol && Math.Abs(x.kv.Key.Length - head.Length) <= tol)
                .OrderBy(x => x.dist).ThenBy(x => x.kv.Key.Length).ToList();
            if (best.Count == 0) continue;
            // Belirsizlik: aynı uzaklıkta farklı komutlara çıkan iki aday varsa tahmin edilmez.
            var top = best.Where(x => x.dist == best[0].dist).Select(x => x.kv.Value.Cmd).Distinct().ToList();
            if (top.Count > 1) continue;
            var k = best[0].kv.Value;
            if (TakesArgs.Contains(k.Cmd) || (shortInput && words.Length == n))
                return new(k.Cmd, k.En, ArgsAfter(n), Fuzzy: true);
        }
        return null;
    }
}

/// <summary>BG14: bellek içi kayan pencere hız sınırı (kullanıcı ve kiracı başına).</summary>
public sealed class SlidingWindowLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new();

    /// <summary>İzin verilirse isteği sayar ve true döner; sınır aşılmışsa false ve bir sonraki uygun an.</summary>
    public (bool Allowed, TimeSpan RetryAfter) Hit(string key, int limit, TimeSpan window, DateTime now)
    {
        var q = _hits.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
            if (q.Count >= limit) return (false, window - (now - q.Peek()));
            q.Enqueue(now);
            return (true, TimeSpan.Zero);
        }
    }

    /// <summary>Uzun süredir boş kalan anahtarları temizler (bellek büyümesin).</summary>
    public void Sweep(TimeSpan window, DateTime now)
    {
        foreach (var kv in _hits)
            lock (kv.Value)
                if (kv.Value.Count == 0 || now - kv.Value.Last() > window) _hits.TryRemove(kv.Key, out _);
    }
}

/// <summary>BG6: sessiz saat kararı (notification-service'in "effective" yanıtından).</summary>
public static class QuietHours
{
    public enum Action { Send, Defer, Skip }

    /// <summary>
    /// Kritik mesaj (ör. güvenlik, adım doğrulaması) her zaman gönderilir. Kişi sohbet kanalını
    /// kapattıysa gönderilmez; sessiz saatteyse bitişine ertelenir.
    /// </summary>
    public static (Action Action, DateTimeOffset? Until) Decide(bool critical, bool chatEnabled, DateTimeOffset? deferUntil, DateTimeOffset now)
    {
        if (critical) return (Action.Send, null);
        if (!chatEnabled) return (Action.Skip, null);
        if (deferUntil is { } u && u > now) return (Action.Defer, u);
        return (Action.Send, null);
    }
}

/// <summary>BG10: düğme değerine verilme zamanı eklenir; eski düğmeler nazik bir mesajla reddedilir.</summary>
public static class ButtonStamp
{
    public static string Encode(string value, DateTimeOffset issued) => $"{value}~{issued.ToUnixTimeSeconds() / 60:x}";

    public static (string Value, DateTimeOffset? Issued) Decode(string? stamped)
    {
        if (string.IsNullOrEmpty(stamped)) return ("", null);
        var i = stamped.LastIndexOf('~');
        if (i < 0 || !long.TryParse(stamped[(i + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var minutes)) return (stamped, null);
        return (stamped[..i], DateTimeOffset.FromUnixTimeSeconds(minutes * 60));
    }

    /// <summary>Damgasız (eski sürüm) düğmeler süresiz kabul edilmez: geçerlilik bilinmiyorsa da süresi dolmuş sayılmaz.</summary>
    public static bool Expired(DateTimeOffset? issued, DateTimeOffset now, int ttlDays) =>
        issued is { } i && ttlDays > 0 && now - i > TimeSpan.FromDays(ttlDays);
}

/// <summary>B6: fiş metninden tutar, tarih, VKN ve kategori önerisi (expense-service ParseReceipt ile aynı kurallar).</summary>
public static class ReceiptParser
{
    public static (decimal? Amount, DateOnly? Date, string? TaxNo) Parse(string text)
    {
        decimal? amount = null; DateOnly? date = null; string? vkn = null;
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var money = new Regex(@"(\d{1,3}(?:[.\s]\d{3})*(?:,\d{2})|\d+(?:[.,]\d{2}))");
        foreach (var l in lines)
        {
            var up = l.ToUpper(CultureInfo.GetCultureInfo("tr-TR"));
            if ((up.Contains("TOPLAM") || up.Contains("TOP ") || up.Contains("TUTAR")) && !up.Contains("KDV") && !up.Contains("ARA"))
            {
                var ms = money.Matches(l);
                if (ms.Count > 0 && TryMoney(ms[^1].Value, out var a)) amount = a;
            }
            var dm = Regex.Match(l, @"\b(\d{2})[./-](\d{2})[./-](\d{4})\b");
            if (date is null && dm.Success && DateOnly.TryParseExact($"{dm.Groups[1].Value}.{dm.Groups[2].Value}.{dm.Groups[3].Value}", "dd.MM.yyyy", out var d)) date = d;
            var vm = Regex.Match(up, @"(?:VKN|V\.?D\.?\s*NO|VERG[İI]\s*NO)\D{0,5}(\d{10,11})");
            if (vkn is null && vm.Success) vkn = vm.Groups[1].Value;
        }
        return (amount, date, vkn);
    }

    private static bool TryMoney(string raw, out decimal value)
    {
        var s = raw.Replace(" ", "");
        if (s.Contains(',')) s = s.Replace(".", "").Replace(',', '.');
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Fiş metninden masraf kategorisi tahmini (expense-service ExpenseCategory adları).</summary>
    public static string GuessCategory(string text)
    {
        var t = ChatText.Fold(text);
        if (Regex.IsMatch(t, @"\b(restoran|lokanta|cafe|kafe|yemek|kahve|burger|pizza|doner|kebap|restaurant)")) return "Meal";
        if (Regex.IsMatch(t, @"\b(otel|hotel|konaklama|pansiyon)")) return "Accommodation";
        if (Regex.IsMatch(t, @"\b(taksi|taxi|otopark|akaryakit|benzin|motorin|opet|shell|petrol|metro|otobus|bilet)")) return "Transport";
        if (Regex.IsMatch(t, @"\b(ucak|havayolu|thy|pegasus|airlines)")) return "Travel";
        if (Regex.IsMatch(t, @"\b(kirtasiye|kagit|toner|ofis)")) return "Supplies";
        if (Regex.IsMatch(t, @"\b(egitim|kurs|seminer|konferans)")) return "Training";
        return "Other";
    }

    public static readonly string[] Categories = { "Meal", "Transport", "Travel", "Accommodation", "Supplies", "Training", "Other" };

    public static string CategoryLabel(string c, bool en) => en ? c switch
    {
        "Meal" => "Meal", "Transport" => "Transport", "Travel" => "Travel", "Accommodation" => "Accommodation",
        "Supplies" => "Supplies", "Training" => "Training", _ => "Other",
    } : c switch
    {
        "Meal" => "Yemek", "Transport" => "Ulaşım", "Travel" => "Seyahat", "Accommodation" => "Konaklama",
        "Supplies" => "Kırtasiye/malzeme", "Training" => "Eğitim", _ => "Diğer",
    };

    /// <summary>"123,45" / "123.45" / "1.234,50" → tutar.</summary>
    public static decimal? ParseAmount(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().Replace("TL", "", StringComparison.OrdinalIgnoreCase).Replace("₺", "").Trim();
        if (s.Contains(',')) s = s.Replace(".", "").Replace(',', '.');
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0 && v <= 1_000_000m ? Math.Round(v, 2) : null;
    }
}

/// <summary>BG16: "peki geçen ay?" gibi devam sorularını önceki soruyla birleştirir.</summary>
public static class FollowUp
{
    private static readonly Regex Lead = new(@"^(peki|ya|ayni|ayni sey|and|what about|how about|ok|tamam)\b\s*", RegexOptions.Compiled);
    private static readonly Regex Period = new(
        @"\b(son \d{1,3} ?(gun|hafta|ay|yil)\w*|(last|past) \d{1,3} ?(day|week|month|year)s?|gecen (ay|yil|sene|hafta)\w*|bu (ay|yil|sene|hafta)\w*|last (month|year|week)|this (month|year|week)|bugun\w*|today|20\d{2}\w*|ocak\w*|subat\w*|mart\w*|nisan\w*|mayis\w*|haziran\w*|temmuz\w*|agustos\w*|eylul\w*|ekim\w*|kasim\w*|aralik\w*|january|february|march|april|may|june|july|august|september|october|november|december)",
        RegexOptions.Compiled);

    public static bool IsFollowUp(string current)
    {
        var f = ChatText.Fold(current);
        return f.Split(' ').Length <= 6 && (Lead.IsMatch(f) || Period.Replace(f, "").Trim().Length == 0);
    }

    /// <summary>Önceki sorudan dönem ifadesini çıkarıp yeni dönemle birleştirir; devam sorusu değilse null.</summary>
    public static string? Merge(string? previous, string current)
    {
        if (string.IsNullOrWhiteSpace(previous) || !IsFollowUp(current)) return null;
        var cur = Lead.Replace(ChatText.Fold(current), "").Trim();
        if (cur.Length == 0) return null;
        var prev = Regex.Replace(Period.Replace(ChatText.Fold(previous), " "), @"\s+", " ").Trim();
        return $"{prev} {cur}".Trim();
    }
}

/// <summary>B7: Mattermost / Rocket.Chat sunucu adresinin yurt dışı SaaS olup olmadığı (KVKK m.9).</summary>
public static class ChatHosts
{
    /// <summary>Sağlayıcının bulut hizmetiyse aktarım anahtarı ("mattermost" / "rocketchat"); kendi sunucunuzsa null.</summary>
    public static string? SaasKey(string platform, string? serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var u)) return null;
        var host = u.Host.TrimEnd('.').ToLowerInvariant();
        return platform switch
        {
            "Mattermost" when host == "cloud.mattermost.com" || host.EndsWith(".cloud.mattermost.com") => "mattermost",
            "RocketChat" when host == "rocket.chat" || host.EndsWith(".rocket.chat") => "rocketchat",
            _ => null,
        };
    }

    /// <summary>Uygulamanın yurt dışı aktarım anahtarı: Slack/Teams her zaman; Mattermost/Rocket.Chat yalnızca bulut sürümünde.</summary>
    public static string? TransferKey(GovernanceService.Models.ChatApp app) => app.Platform switch
    {
        "Slack" => "slack",
        "Teams" => "microsoft",
        "Mattermost" or "RocketChat" => SaasKey(app.Platform, app.ServerUrl),
        _ => app.Platform.ToLowerInvariant(),
    };

    public static bool Allowed(GovernanceService.Models.ChatApp app, HashSet<string> allowed) =>
        TransferKey(app) is not { } k || allowed.Contains(k);
}

/// <summary>BG20: yönetilebilir komut/özellik anahtarları ve komut → özellik eşlemesi.</summary>
public static class ChatFeatureCatalog
{
    public static readonly (string Key, string Label, string LabelEn)[] All =
    {
        ("approvals", "Onaylar ve toplu onay", "Approvals and bulk approval"),
        ("balance", "İzin bakiyesi", "Leave balance"),
        ("leave", "İzin talebi", "Leave request"),
        ("leavecancel", "İzin iptali", "Leave cancellation"),
        ("home", "Özet", "Summary"),
        ("onleave", "İzindekiler", "On leave"),
        ("whereabouts", "Kim nerede", "Whereabouts"),
        ("assistant", "İK asistanı ve rapor soruları", "HR assistant and report questions"),
        ("expense", "Fişten masraf", "Expense from receipt"),
        ("kudos", "Teşekkür (takdir)", "Thanks (kudos)"),
        ("desk", "Masa rezervasyonu", "Desk booking"),
        ("shifts", "Vardiya ve takas", "Shifts and swaps"),
        ("clock", "Giriş-çıkış", "Clock in/out"),
        ("announcements", "Duyurular", "Announcements"),
        ("documents", "Belge talebi", "Document request"),
        ("payslip", "Bordro özeti (adım doğrulamalı)", "Payslip summary (step-up)"),
        ("hrcase", "İK vakası", "HR case"),
        ("pulse", "Nabız anketi", "Pulse survey"),
        ("onboarding", "İşe başlama mesajları", "Onboarding messages"),
        ("celebrations", "Doğum günü / yıldönümü", "Birthdays / anniversaries"),
        ("oneonone", "Birebir hatırlatmaları", "1:1 reminders"),
        ("exit", "Çıkış anketi", "Exit survey"),
    };

    public static readonly HashSet<string> Keys = All.Select(x => x.Key).ToHashSet();

    public static string? FeatureOf(string cmd) => cmd switch
    {
        "approvals" or "pending" => "approvals",
        "balance" => "balance",
        "leave" => "leave",
        "cancelleave" => "leavecancel",
        "home" => "home",
        "onleave" => "onleave",
        "whereabouts" => "whereabouts",
        "assistant" => "assistant",
        "expense" => "expense",
        "kudos" => "kudos",
        "desk" => "desk",
        "shifts" or "swaps" => "shifts",
        "clockin" or "clockout" => "clock",
        "announcements" => "announcements",
        "documents" => "documents",
        "payslip" => "payslip",
        "hrcase" => "hrcase",
        "anniversary" => "celebrations",
        _ => null,
    };
}
