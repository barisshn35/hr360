using System.Globalization;
using System.Text;

namespace RecruitmentService.Services;

/// <summary>
/// G13: tekrar aday tespiti (saf mantık, birim testli).
///
/// Kurallar:
///  * E-posta: küçük harf, boşluksuz, yerel kısımdaki "+etiket" atılır (ali+is@x.com = ali@x.com).
///    Eşleşme KESİN tekrardır.
///  * Telefon: yalnızca rakamlar, son 10 hane (+90 / 0 öneki farkı yok sayılır).
///    Telefon + benzer ad (Türkçe harf katlamalı, en fazla 2 düzenleme farkı ya da ad/soyad yer
///    değiştirmiş) KESİN tekrardır; ad çok farklıysa yalnızca OLASI tekrardır (aile/ortak hat).
/// </summary>
public static class DuplicateDetector
{
    public enum Strength { None, Possible, Strong }

    public sealed record Candidate(Guid Id, string FirstName, string LastName, string? NormalizedEmail, string? NormalizedPhone);

    public sealed record Match(Guid CandidateId, Strength Strength, string Reason);

    public static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var e = email.Trim().ToLowerInvariant();
        var at = e.LastIndexOf('@');
        if (at <= 0) return e;
        var local = e[..at];
        var plus = local.IndexOf('+');
        if (plus > 0) local = local[..plus];
        return local + e[at..];
    }

    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length < 7) return null;
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    /// <summary>Türkçe harfleri katlar, küçük harfe çevirir, harf dışı karakterleri tek boşluğa indirir.</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var lower = name.Trim().ToLower(new CultureInfo("tr-TR"));
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            var c = ch switch
            {
                'ı' => 'i', 'ğ' => 'g', 'ü' => 'u', 'ş' => 's', 'ö' => 'o', 'ç' => 'c', 'â' => 'a', 'î' => 'i', 'û' => 'u',
                _ => ch,
            };
            sb.Append(char.IsLetter(c) ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Ad-soyad benzer mi? (yazım hatası ≤2, ad/soyad yer değiştirmiş ya da ikinci ad eksik)</summary>
    public static bool NamesSimilar(string firstA, string lastA, string firstB, string lastB)
    {
        var a = NormalizeName($"{firstA} {lastA}");
        var b = NormalizeName($"{firstB} {lastB}");
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        var tolerance = Math.Max(a.Length, b.Length) >= 10 ? 2 : 1;
        if (Levenshtein(a, b) <= tolerance) return true;
        var ta = a.Split(' ').ToHashSet();
        var tb = b.Split(' ').ToHashSet();
        if (ta.SetEquals(tb)) return true; // "Ali Veli" ~ "Veli Ali"
        // İkinci ad: "Ayşe Nur Yılmaz" ~ "Ayşe Yılmaz" (soyad aynı, ilk ad aynı)
        var lastSame = NormalizeName(lastA) == NormalizeName(lastB);
        var firstTokenSame = NormalizeName(firstA).Split(' ')[0] == NormalizeName(firstB).Split(' ')[0];
        return lastSame && firstTokenSame;
    }

    /// <summary>Girdiye en güçlü eşleşmeyi döner (yoksa null).</summary>
    public static Match? FindBest(IEnumerable<Candidate> existing, string firstName, string lastName, string? email, string? phone)
    {
        var ne = NormalizeEmail(email);
        var np = NormalizePhone(phone);
        Match? best = null;
        foreach (var c in existing)
        {
            Match? m = null;
            if (ne is not null && c.NormalizedEmail == ne)
                m = new Match(c.Id, Strength.Strong, "email");
            else if (np is not null && c.NormalizedPhone == np)
                m = NamesSimilar(firstName, lastName, c.FirstName, c.LastName)
                    ? new Match(c.Id, Strength.Strong, "phone+name")
                    : new Match(c.Id, Strength.Possible, "phone");
            if (m is null) continue;
            if (best is null || Rank(m) > Rank(best)) best = m;
        }
        return best;
    }

    private static int Rank(Match m) => m.Strength switch
    {
        Strength.Strong when m.Reason == "email" => 3,
        Strength.Strong => 2,
        Strength.Possible => 1,
        _ => 0,
    };

    public static string Describe(string reason) => reason switch
    {
        "email" => "Aynı e-posta adresiyle kayıtlı aday var",
        "phone+name" => "Aynı telefon ve benzer ad-soyadla kayıtlı aday var",
        "phone" => "Aynı telefonla kayıtlı (farklı adlı) aday var",
        _ => "Olası tekrar aday",
    };
}
