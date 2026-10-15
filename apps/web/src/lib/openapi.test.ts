import { describe, expect, it } from 'vitest'
import { toGatewaySpec } from './openapi'

describe('toGatewaySpec', () => {
  it('servis yollarını gateway adresine çevirir, iç ve /api dışı uçları çıkarır', () => {
    const out = toGatewaySpec('workflow', {
      openapi: '3.0.1',
      paths: {
        '/api/workflows': { get: {} },
        '/api/workflows/{id}': { get: {} },
        '/api/internal/workflows/{id}/steps/{stepId}/decide': { post: {} },
        '/health': { get: {} },
        '/internal/smtp-config/{slug}': { get: {} },
      },
    }, '')
    expect(Object.keys(out.paths!)).toEqual(['/workflows', '/workflows/{id}'])
    expect(out.servers).toEqual([{ url: '/api/workflow' }])
    expect(out.openapi).toBe('3.0.1')
  })
})
