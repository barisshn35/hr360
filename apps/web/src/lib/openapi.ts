import { env } from '@/lib/env'

export type Spec = { paths?: Record<string, unknown>; servers?: { url: string }[]; [k: string]: unknown }

/** Servis içi yolları gateway adresine çevirir; gateway'den erişilemeyen uçları çıkarır. */
export function toGatewaySpec(service: string, spec: Spec, base = env.apiBase): Spec {
  const paths: Record<string, unknown> = {}
  for (const [path, item] of Object.entries(spec.paths ?? {})) {
    if (!path.startsWith('/api/') || path.startsWith('/api/internal/')) continue
    paths[path.slice(4)] = item
  }
  return { ...spec, servers: [{ url: `${base}/api/${service}` }], paths }
}

