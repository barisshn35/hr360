import { apiFetch } from './client'
import type { MlHealth } from './types'

export const mlApi = {
  health: (signal?: AbortSignal) => apiFetch<MlHealth>('/ml/health', { signal, anonymous: true }),

  // Kişi bazlı risk tahmini KVKK m.11 nedeniyle governanceApi.attritionRisk üzerinden yapılır;
  // /ml/predict ve /ml/explain gateway'de dışarıya kapalıdır.
}

export const gatewayApi = {
  health: (signal?: AbortSignal) =>
    apiFetch<{ status: string; service: string }>('/gateway/health', { signal, anonymous: true }),
}
