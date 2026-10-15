using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LeaveService.Infrastructure;

/// <summary>Sayfalı liste yanıtı: <c>{ items, total, page, pageSize }</c>.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>
/// Liste uçları için sunucu tarafı sayfalama (G24).
///
/// Geriye uyum: <c>page</c> verilmezse eski yanıt biçimi (düz dizi, tüm kayıtlar) döner;
/// kayıt sayısı <c>X-Total-Count</c> başlığındadır. Diğer servisler (kullanıcının jetonuyla)
/// ve eski istemciler bu biçime bağlı. <c>page</c> verilirse <see cref="PagedResult{T}"/> döner
/// (<c>pageSize</c> en fazla <see cref="MaxPageSize"/>), toplam yine <c>X-Total-Count</c>'ta.
/// </summary>
public static class Paging
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 200;

    public static async Task<IActionResult> ListAsync<T>(ControllerBase c, IQueryable<T> ordered, int? page, int? pageSize,
        CancellationToken ct)
    {
        if (page is null)
        {
            var list = await ordered.ToListAsync(ct);
            SetTotal(c, list.Count);
            return c.Ok(list);
        }

        var (p, size) = Normalize(page.Value, pageSize);
        var count = await ordered.CountAsync(ct);
        var items = count == 0 ? new List<T>() : await ordered.Skip((p - 1) * size).Take(size).ToListAsync(ct);
        SetTotal(c, count);
        return c.Ok(new PagedResult<T>(items, count, p, size));
    }

    /// <summary>Sayfa 1..1 000 000, boyut 1..<see cref="MaxPageSize"/> (verilmezse <see cref="DefaultPageSize"/>).</summary>
    public static (int Page, int Size) Normalize(int page, int? pageSize) =>
        (Math.Clamp(page, 1, 1_000_000), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));

    public static void SetTotal(ControllerBase c, int total) =>
        c.Response.Headers["X-Total-Count"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Arama deseni: SQL'deki hr360_fold ile aynı katlama (küçük harf, ı/ş/ğ/ü/ö/ç → i/s/g/u/o/c),
    /// % ve _ kaçışlı (kaçış karakteri ters bölü). <c>LIKE Fold(sütun)</c> ile kullanılır.
    /// </summary>
    public static string FoldLike(string term) => Like(Fold(term));

    public static string Fold(string value)
    {
        var s = value.Replace('İ', 'i').Replace('I', 'i').ToLowerInvariant();
        var map = new Dictionary<char, char> { ['ı'] = 'i', ['ş'] = 's', ['ğ'] = 'g', ['ü'] = 'u', ['ö'] = 'o', ['ç'] = 'c', ['â'] = 'a', ['î'] = 'i', ['û'] = 'u' };
        return string.Concat(s.Select(ch => map.TryGetValue(ch, out var r) ? r : ch));
    }

    /// <summary>ILIKE deseni: % ve _ kaçışlı (kaçış karakteri ters bölü).</summary>
    public static string Like(string term) =>
        "%" + term.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public static bool Desc(string? dir) => string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);

    /// <summary>Virgülle ayrılmış kimlik listesi (geçersizler atlanır, en fazla 200).</summary>
    public static List<Guid> Guids(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().Take(200).ToList();
}
