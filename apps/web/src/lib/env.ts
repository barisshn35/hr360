/** Ortam değişkenleri tek noktadan, güvenli varsayılanlarla okunur. */
const raw = import.meta.env

export const env = {
  keycloakUrl: (raw.VITE_KEYCLOAK_URL as string | undefined) || '/auth',
  keycloakRealm: (raw.VITE_KEYCLOAK_REALM as string | undefined) || 'hr360',
  keycloakClientId: (raw.VITE_KEYCLOAK_CLIENT_ID as string | undefined) || 'hr360-web',
  /** Boşsa aynı origin (prod'da nginx gateway) kullanılır. */
  apiBase: ((raw.VITE_API_BASE as string | undefined) || '').replace(/\/$/, ''),
} as const
