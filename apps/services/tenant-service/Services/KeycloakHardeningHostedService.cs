namespace TenantService.Services;

/// <summary>
/// Açılışta Keycloak giriş akışına passkey/güvenlik anahtarı (WebAuthn) ikinci adımını
/// ekler (G22). KEYCLOAK_PASSKEYS=false ile kapatılır. Keycloak henüz ayakta değilse
/// birkaç kez yeniden dener; başarısız olursa giriş akışı olduğu gibi kalır.
/// </summary>
public sealed class KeycloakHardeningHostedService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<KeycloakHardeningHostedService> _log;
    public KeycloakHardeningHostedService(IServiceProvider sp, ILogger<KeycloakHardeningHostedService> log) { _sp = sp; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("KEYCLOAK_PASSKEYS"), "false", StringComparison.OrdinalIgnoreCase)) return;
        for (var attempt = 1; attempt <= 20 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var kc = scope.ServiceProvider.GetRequiredService<KeycloakAdminClient>();
                var changed = await kc.EnsurePasskeyFlowAsync(ct);
                _log.LogInformation("Passkey (WebAuthn) giriş adımı hazır{Changed}", changed ? " (akış güncellendi)" : "");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Passkey akışı ayarlanamadı (deneme {Attempt}): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * attempt)), ct);
            }
        }
    }
}
