import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import SwaggerUI from 'swagger-ui-react'
import 'swagger-ui-react/swagger-ui.css'
import { BookOpenText, Globe2, LoaderCircle } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { EmptyState, ErrorState, InfoNote } from '@/components/ui/States'
import { getValidToken } from '@/auth/keycloak'
import { env } from '@/lib/env'
import { cn } from '@/lib/utils'
import { toGatewaySpec, type Spec } from '@/lib/openapi'
import { translateServerData, tx } from '@/lib/i18n'

/**
 * Birleşik API belgesi: her servisin OpenAPI tanımı gateway'den
 * (/api-docs/specs/<servis>.json, yalnızca oturum açmış kullanıcıya) alınır ve
 * gateway'deki gerçek adreslere çevrilir: servis içindeki "/api/leave-requests"
 * dışarıdan "/api/leave/leave-requests"tir. "Dene" düğmesi oturumdaki jetonla
 * istek atar; yani kullanıcı yalnızca zaten yetkili olduğu işlemleri yapabilir.
 */

type Doc = { id: string; title: string; detail: string; public?: boolean }

const DOCS: Doc[] = [
  { id: 'public', title: tx('Açık API (v1)'), detail: tx('Dış sistemler için, X-Api-Key ile'), public: true },
  { id: 'employee', title: tx('Çalışanlar'), detail: tx('Çalışan kaydı, atamalar') },
  { id: 'organization', title: tx('Organizasyon'), detail: tx('Şirket, departman, ekip') },
  { id: 'leave', title: tx('İzin'), detail: tx('Bakiye, talep, resmi tatil') },
  { id: 'workflow', title: tx('Onay akışları'), detail: tx('Adımlar, vekâlet, SLA') },
  { id: 'expense', title: tx('Masraf ve İK vakaları'), detail: tx('Beyan, belge, vaka') },
  { id: 'timeshift', title: tx('Vardiya ve puantaj'), detail: tx('Vardiya, giriş/çıkış') },
  { id: 'performance', title: tx('Performans'), detail: tx('Dönem, hedef, değerlendirme') },
  { id: 'learning', title: tx('Eğitim'), detail: tx('Katalog, kayıt, sertifika') },
  { id: 'compensation', title: tx('Ücret'), detail: tx('Ücret geçmişi, bant') },
  { id: 'recruitment', title: tx('İşe alım'), detail: tx('İlan, aday, mülakat') },
  { id: 'onboarding', title: tx('İşe başlangıç'), detail: tx('Plan, görev, zimmet') },
  { id: 'notification', title: tx('Bildirim'), detail: tx('Gelen kutusu, şablon') },
  { id: 'engagement', title: tx('Bağlılık'), detail: tx('Takdir, anket, 1:1, ofis') },
  { id: 'governance', title: tx('Yönetişim'), detail: tx('Denetim, kural, entegrasyon, AI') },
  { id: 'tenant', title: tx('Kiracı'), detail: tx('Şirket hesabı, ekip, marka') },
]

async function loadSpec(doc: Doc): Promise<Spec> {
  if (doc.public) {
    const res = await fetch(`${env.apiBase}/api/governance/public/v1/openapi.json`)
    if (!res.ok) throw new Error(tx('Açık API tanımı alınamadı (HTTP {0}).', [res.status]))
    return translateServerData((await res.json()) as Spec)
  }
  const token = await getValidToken()
  const res = await fetch(`${env.apiBase}/api-docs/specs/${doc.id}.json`, {
    headers: token ? { Authorization: `Bearer ${token}` } : {},
  })
  if (!res.ok)
    throw new Error(
      res.status >= 500
        ? tx('Servis API tanımını üretemedi (HTTP {0}). Servis günlüğüne bakın ya da daha sonra yeniden deneyin.', [res.status])
        : tx('API tanımı alınamadı (HTTP {0}).', [res.status]),
    )
  return toGatewaySpec(doc.id, (await res.json()) as Spec)
}

export function ApiDocsPage() {
  const [active, setActive] = useState<Doc>(DOCS[0])
  const spec = useQuery({ queryKey: ['api-docs', active.id], queryFn: () => loadSpec(active), staleTime: 5 * 60_000, retry: false })
  const count = useMemo(() => Object.keys(spec.data?.paths ?? {}).length, [spec.data])

  return (
    <>
      <PageHeader
        title={tx('API belgeleri')}
        description={tx('Tüm servislerin uçları tek yerde. Panelden yaptığınız her işlem bu uçlarla yapılır.')}
      />
      <div className="grid gap-6 xl:grid-cols-[280px_1fr]">
        <Panel className="h-fit">
          <PanelHead title={tx('Servisler')} />
          <PanelBody className="grid gap-1 p-2">
            {DOCS.map((d) => (
              <button
                key={d.id}
                type="button"
                onClick={() => setActive(d)}
                aria-current={active.id === d.id ? 'true' : undefined}
                className={cn(
                  'flex cursor-pointer items-start gap-3 rounded-xl px-3 py-2.5 text-left transition-colors',
                  active.id === d.id ? 'bg-primary/10 text-foreground' : 'text-muted-foreground hover:bg-card hover:text-foreground',
                )}
              >
                {d.public ? <Globe2 className="mt-0.5 size-4 shrink-0 text-primary" /> : <BookOpenText className="mt-0.5 size-4 shrink-0" />}
                <span>
                  <span className="block text-[13.5px] font-medium">{d.title}</span>
                  <span className="block text-[12px] text-muted-foreground">{d.detail}</span>
                </span>
              </button>
            ))}
          </PanelBody>
        </Panel>

        <div className="grid min-w-0 gap-4">
          <InfoNote>
            {active.public
              ? tx('Açık API, Entegrasyonlar ekranında oluşturulan X-Api-Key ile çağrılır. "Authorize" düğmesine anahtarı girerek deneyebilirsiniz; dakikada 120 istek sınırı vardır.')
              : tx('"Try it out" istekleri oturumunuzun yetkisiyle gider: rolünüzün izin vermediği uçlar 403 döner. Adresler gateway\'e göredir ({0}/api/{1}/…).', [env.apiBase || tx('bu sunucu'), active.id])}
          </InfoNote>
          <Panel className="min-w-0 overflow-hidden">
            <PanelHead title={active.title} note={spec.data ? tx('{0} uç', [count]) : undefined} />
            <PanelBody className="min-w-0 p-0">
              {spec.isPending && (
                <div className="flex items-center gap-2 p-6 text-sm text-muted-foreground">
                  <LoaderCircle className="size-4 animate-spin" />{' '}{tx('Tanım yükleniyor')}
                </div>
              )}
              {spec.isError && <ErrorState title={tx('API tanımı alınamadı')} message={(spec.error as Error).message} onRetry={() => void spec.refetch()} />}
              {spec.data && count === 0 && (
                <EmptyState title={tx('Bu serviste gateway üzerinden erişilebilir uç yok')} detail={tx('Tanım yüklendi ancak listelenecek uç bulunamadı.')} />
              )}
              {spec.data && count > 0 && (
                // Swagger UI kendi açık temasını kullanır; okunaklı kalması için beyaz zemin.
                <div className="hr360-swagger rounded-b-2xl bg-white text-black">
                  <SwaggerUI
                    key={active.id}
                    spec={spec.data}
                    docExpansion="list"
                    defaultModelsExpandDepth={0}
                    tryItOutEnabled={false}
                    requestInterceptor={async (req) => {
                      if (!active.public) {
                        const token = await getValidToken()
                        if (token) req.headers = { ...req.headers, Authorization: `Bearer ${token}` }
                      }
                      return req
                    }}
                  />
                </div>
              )}
            </PanelBody>
          </Panel>
        </div>
      </div>
    </>
  )
}
