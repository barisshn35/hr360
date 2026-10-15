import { describe, expect, it } from 'vitest'
import { OFFLINE_MAX_AGE_DAYS, isExpired, isQueueable, queueOutcome, queuedKind, queuedSummary, type QueuedRequest } from './push'

const q = (path: string, body: unknown, at = new Date().toISOString()): QueuedRequest => ({ id: 'x', path, method: 'POST', body, at })

describe('çevrimdışı taslak kuyruğu (dalga 12)', () => {
  it('yalnızca izin, fazla mesai ve masraf oluşturma sıraya alınır', () => {
    expect(isQueueable('/api/leave/leave-requests', 'POST')).toBe(true)
    expect(isQueueable('/api/expense/expense-claims', 'POST')).toBe(true)
    expect(isQueueable('/api/timeshift/overtime', 'POST')).toBe(true)
    expect(isQueueable('/api/expense/expense-claims', 'GET')).toBe(false)
    expect(isQueueable('/api/expense/expense-claims/1/submit', 'POST')).toBe(false)
    expect(isQueueable('/api/expense/receipts', 'POST')).toBe(false)
    expect(queuedKind('/api/expense/expense-claims')).toBe('expense')
  })

  it('gönderim sonucu: 4xx kuyruktan çıkar, ağ/5xx yeniden denenir', () => {
    expect(queueOutcome(null)).toBe('sent')
    expect(queueOutcome(400)).toBe('rejected')
    expect(queueOutcome(409)).toBe('rejected')
    expect(queueOutcome(0)).toBe('retry')
    expect(queueOutcome(503)).toBe('retry')
  })

  it('eski taslak süresi dolar', () => {
    const now = Date.parse('2026-10-20T00:00:00Z')
    expect(isExpired(q('/api/leave/leave-requests', {}, '2026-10-19T00:00:00Z'), now)).toBe(false)
    expect(isExpired(q('/api/leave/leave-requests', {}, new Date(now - (OFFLINE_MAX_AGE_DAYS + 1) * 86_400_000).toISOString()), now)).toBe(true)
  })

  it('kısa açıklama', () => {
    expect(queuedSummary(q('/api/leave/leave-requests', { startDate: '2026-11-02', endDate: '2026-11-04' }))).toBe('2026-11-02 – 2026-11-04')
    expect(queuedSummary(q('/api/expense/expense-claims', { title: 'Ankara ziyareti' }))).toBe('Ankara ziyareti')
  })
})
