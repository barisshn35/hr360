namespace NotificationService.Tenancy;

/// <summary>
/// Tenant'a ait varliklar bunu uygular. DbContext, bu arayuzu uygulayan
/// TUM varliklara otomatik global query filter ekler - tek tek yazmaya
/// gerek kalmaz, boylece "bir yerde filtreyi unutup veri sizdirma" riski
/// yapisal olarak ortadan kalkar.
/// </summary>
public interface ITenantOwned
{
    string TenantSlug { get; set; }
}
