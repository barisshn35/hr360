/**
 * `/panel/performans/degerlendirme/yeni?calisan=&donem=&tur=` — derin bağlantı.
 * Parametreler tamsa taslağı oluşturup doğrudan forma yönlendirir; aynı
 * türde taslak zaten varsa ona gider. Eksikse başlatma penceresini açar.
 */

import { useEffect, useRef, useState } from 'react'
import { Navigate, useNavigate, useSearchParams } from 'react-router-dom'
import { ApiError } from '@/api/client'
import { REVIEW_TYPES, useCreateReview, useMyEmployeeId, type ReviewType } from '@/api/performance'
import { useAuth } from '@/auth/useAuth'
import { Panel } from '@/components/ui/Panel'
import { CenteredSpinner, ErrorState } from '@/components/ui/States'
import { errorText } from '../components/controls'
import { useCurrentCycle } from '../hooks'
import { StartReviewDialog } from './StartReviewDialog'
import { tx } from '@/lib/i18n'

export function NewReviewPage() {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const { can } = useAuth()
  const me = useMyEmployeeId()
  const { cycles, isPending } = useCurrentCycle()
  const create = useCreateReview()
  const started = useRef(false)
  const [error, setError] = useState<string | null>(null)

  const employeeId = params.get('calisan')
  const cycleId = params.get('donem')
  const typeParam = params.get('tur') as ReviewType | null
  const type = typeParam && REVIEW_TYPES.includes(typeParam) ? typeParam : null
  const complete = Boolean(employeeId && cycleId && type)

  useEffect(() => {
    if (!complete || !me.employeeId || started.current) return
    started.current = true
    create.mutate(
      { employeeId: employeeId!, cycleId: cycleId!, type: type!, reviewerEmployeeId: me.employeeId },
      {
        onSuccess: (r) => navigate(`/panel/performans/degerlendirme/${r.id}`, { replace: true }),
        onError: (e) => {
          const existing = e instanceof ApiError ? (e.detail as { existingReviewId?: string } | undefined)?.existingReviewId : undefined
          if (existing) navigate(`/panel/performans/degerlendirme/${existing}`, { replace: true })
          else setError(errorText(e))
        },
      },
    )
  }, [complete, me.employeeId, employeeId, cycleId, type, create, navigate])

  if (error) {
    return (
      <div className="mx-auto w-full max-w-3xl">
        <Panel>
          <ErrorState title={tx('Değerlendirme başlatılamadı')} message={error} />
        </Panel>
      </div>
    )
  }
  if (complete || isPending || me.isPending) return <CenteredSpinner label={tx('Değerlendirme hazırlanıyor')} />
  if (!me.employeeId) return <Navigate to="/panel/performans/degerlendirme" replace />

  return (
    <StartReviewDialog
      cycles={cycles}
      defaultCycleId={cycleId ?? ''}
      me={me.employeeId}
      manager={can('performance:manage')}
      preset={{ employeeId: employeeId ?? undefined, type: type ?? undefined }}
      onClose={() => navigate('/panel/performans/degerlendirme', { replace: true })}
    />
  )
}
