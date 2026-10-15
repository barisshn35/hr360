<#-- HR360: Keycloak hata sayfasi. Tabandaki error.ftl ile ayni alanlar; uygulamadaki
     hata ekranlari ailesiyle ayni gorunum (hareketli kalkan + kirik baglanti, saf CSS). -->
<#import "template.ftl" as layout>
<@layout.registrationLayout displayMessage=false; section>
    <#if section = "header">
        ${kcSanitize(msg("errorTitle"))?no_esc}
    <#elseif section = "form">
        <div id="kc-error-message" class="hr-error">
            <div class="hr-error-art" aria-hidden="true">
                <span class="hr-error-ring"></span>
                <span class="hr-error-tile">
                    <svg viewBox="0 0 48 48" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round">
                        <path class="hr-error-link-a" d="M20 28l-4 4a6 6 0 0 1-8.5-8.5l4-4a6 6 0 0 1 8.5 0"/>
                        <path class="hr-error-link-b" d="M28 20l4-4a6 6 0 0 1 8.5 8.5l-4 4a6 6 0 0 1-8.5 0"/>
                        <path class="hr-error-spark" d="M22 14v-4M14 22h-4M26 34v4M34 26h4"/>
                    </svg>
                </span>
            </div>
            <p class="instruction">${kcSanitize(message.summary)?no_esc}</p>
            <div class="hr-error-actions">
                <#if skipLink??>
                <#else>
                    <#if client?? && client.baseUrl?has_content>
                        <a id="backToApplication" class="hr-error-btn" href="${client.baseUrl}">${kcSanitize(msg("backToApplication"))?no_esc}</a>
                    <#else>
                        <a id="backToApplication" class="hr-error-btn" href="/giris">${kcSanitize(msg("hr360BackToSignIn"))?no_esc}</a>
                    </#if>
                </#if>
            </div>
        </div>
    </#if>
</@layout.registrationLayout>
