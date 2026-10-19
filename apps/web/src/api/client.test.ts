// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, apiFetch, isModuleNotInstalledError } from './client'
import { setDictionary } from '@/lib/i18n'

setDictionary({}, 'tr')

function respond(status: number, body: unknown) {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })))
}

afterEach(() => vi.unstubAllGlobals())

describe('kapalı modül yanıtı', () => {
  it('module_not_installed 404 dostça iletiye çevrilir', async () => {
    respond(404, { code: 'module_not_installed', module: 'recruitment' })
    const err = await apiFetch('/api/recruitment/job-postings', { anonymous: true }).catch((e: unknown) => e)
    expect(err).toBeInstanceOf(ApiError)
    expect((err as ApiError).status).toBe(404)
    expect((err as ApiError).message).toBe('Bu modül bu kurulumda açık değil.')
    expect(isModuleNotInstalledError(err)).toBe(true)
  })
  it('sıradan 404 eskisi gibi kalır', async () => {
    respond(404, { title: 'Not Found' })
    const err = await apiFetch('/api/leave/leave-requests/x', { anonymous: true }).catch((e: unknown) => e)
    expect((err as ApiError).message).toBe('Kayıt bulunamadı.')
    expect(isModuleNotInstalledError(err)).toBe(false)
  })
})
