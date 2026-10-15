using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExpenseService.Tenancy;

/// <summary>
/// DbContext'in tenant degerlerini EF'in gorebilecegi sekilde acar.
///
/// KRITIK: Global query filter, tenant degerini DbContext ORNEGININ uyesinden
/// okumak zorundadir. Ayri bir servis nesnesi (ITenantContext) dogrudan
/// yakalanirsa, EF modeli tip basina onbellege aldigi icin ILK olusturulan
/// baglam sonsuza kadar kullanilir ve filtre calismaz.
/// EF, DbContext ornegine yapilan uye erisimini taniyip her sorguda yeniden
/// degerlendirir - bu yuzden degerler buradan gecmelidir.
/// </summary>
public interface ITenantAwareContext
{
    string? CurrentTenantSlug { get; }
    bool CurrentIsPlatformAdmin { get; }
}

public static class TenantDbContextExtensions
{
    /// <summary>
    /// ITenantOwned uygulayan tum varliklara global query filter ekler.
    /// OnModelCreating'in SONUNDA, context'in kendisi verilerek cagrilir:
    ///     modelBuilder.ApplyTenantFilters(this);
    ///
    /// Filtre mantigi:
    ///   - platform-admin ise: filtre yok (tum veriyi gorur)
    ///   - tenant cozuldu ise: yalnizca kendi tenant'inin verisi
    ///   - cozulemedi ise: HICBIR SEY (guvenli varsayilan)
    /// </summary>
    public static void ApplyTenantFilters<TContext>(
        this ModelBuilder modelBuilder, TContext context)
        where TContext : DbContext, ITenantAwareContext
    {
        // Sabit, context ORNEGININ kendisidir; EF bunu tanir ve her sorguda
        // guncel degeri okur (bkz. yukaridaki not).
        var contextExpr = Expression.Constant(context, typeof(TContext));
        var slugExpr = Expression.Property(
            contextExpr, nameof(ITenantAwareContext.CurrentTenantSlug));
        var adminExpr = Expression.Property(
            contextExpr, nameof(ITenantAwareContext.CurrentIsPlatformAdmin));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType)) continue;

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            var property = Expression.Property(parameter, nameof(ITenantOwned.TenantSlug));

            // platform-admin VEYA e.TenantSlug == context.CurrentTenantSlug
            var body = Expression.OrElse(
                adminExpr,
                Expression.Equal(property, slugExpr));

            modelBuilder.Entity(entityType.ClrType)
                .HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }

    /// <summary>
    /// Kaydetmeden once yeni varliklarin TenantSlug'ini otomatik doldurur.
    /// SaveChangesAsync override'inda cagrilir - gelistiricinin her yerde
    /// elle atamasi gerekmez.
    /// </summary>
    public static void StampTenant(this DbContext db, ITenantContext tenant)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantSlug)) return;

        foreach (var entry in db.ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State == EntityState.Added &&
                string.IsNullOrWhiteSpace(entry.Entity.TenantSlug))
            {
                entry.Entity.TenantSlug = tenant.TenantSlug;
            }
        }
    }

    /// <summary>Tenant sutununu ve indeksini standart sekilde yapilandirir.</summary>
    public static void ConfigureTenantColumn<T>(this EntityTypeBuilder<T> builder)
        where T : class, ITenantOwned
    {
        builder.Property(e => e.TenantSlug).IsRequired().HasMaxLength(64);
        builder.HasIndex(e => e.TenantSlug);
    }
}
