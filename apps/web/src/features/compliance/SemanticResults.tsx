import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { Sparkles } from 'lucide-react'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Modal } from '@/components/ui/Modal'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { EmptyState, RowsSkeleton } from '@/components/ui/States'
import { governanceApi } from '@/api/governance'
import { mlInsightsApi, type SemanticHit } from '@/api/mlInsights'
import { semanticSourceLabel } from '@/lib/mlInsights'
import { tx } from '@/lib/i18n'
import { errMsg } from '@/features/shared/kit'

/** Bilgi bankası maddesini (herkese açık) okuma penceresi. */
function KbViewer({ id, onClose }: { id: string; onClose: () => void }) {
  const q = useQuery({ queryKey: ['kb', 'all'], queryFn: ({ signal }) => governanceApi.kb(signal), staleTime: 60_000 })
  const a = q.data?.find((x) => x.id === id)
  return (
    <Modal open onClose={onClose} title={a?.title ?? tx('Bilgi bankası')} note={tx('Bilgi bankası')}>
      {q.isPending ? <RowsSkeleton rows={3} /> : a ? <p className="whitespace-pre-line text-[13.5px] leading-relaxed">{a.body}</p> : <p className="text-[13px] text-muted-foreground">{tx('Madde bulunamadı.')}</p>}
    </Modal>
  )
}

/**
 * Anlamsal arama sonuçları (ML dalgası 2, madde 50): bilgi bankası, doküman kütüphanesi ve duyurularda
 * anlamca yakın metinler (TF-IDF + karakter n-gram + LSA; büyük model yok). Yalnızca görme yetkiniz
 * olan belgeler döner — süzgeç sunucudadır.
 */
export function SemanticResults({ query, onOpenDoc }: { query: string; onOpenDoc: (id: string) => void }) {
  const nav = useNavigate()
  const [kb, setKb] = useState<string | null>(null)
  const q = useQuery({ queryKey: ['semantic', query], queryFn: ({ signal }) => mlInsightsApi.semanticSearch(query, signal), enabled: query.length >= 2, retry: false, staleTime: 60_000 })
  const open = (h: SemanticHit) => (h.source === 'library' ? onOpenDoc(h.id) : h.source === 'kb' ? setKb(h.id) : nav('/panel/duyurular'))
  return (
    <Panel>
      <PanelHead title={<span className="flex items-center gap-2"><Sparkles className="size-4 text-primary" />{' '}{tx('Anlamsal arama sonuçları')}</span>}
        note={tx('Kelimesi kelimesine değil, anlamca yakın sonuçlar: bilgi bankası, doküman kütüphanesi ve duyurular.')} />
      <PanelBody className="p-0">
        {q.isPending ? <div className="p-5"><RowsSkeleton rows={3} /></div> : q.isError ? (
          <p className="p-5 text-[13px] text-muted-foreground">{errMsg(q.error, tx('Anlamsal arama şu anda kullanılamıyor.'))}</p>
        ) : !q.data?.hits.length ? <EmptyState icon={Sparkles} title={tx('Sonuç yok')} /> : (
          <ul className="divide-y divide-border">
            {q.data.hits.map((h) => (
              <li key={`${h.source}:${h.id}`}>
                <button type="button" className="w-full px-5 py-3 text-left text-[13px] hover:bg-muted/40" onClick={() => open(h)}>
                  <span className="flex flex-wrap items-center gap-2">
                    <span className="font-medium">{h.title}</span>
                    <StatusBadge>{semanticSourceLabel(h.source)}</StatusBadge>
                  </span>
                  <span className="mt-1 block text-[12.5px] text-muted-foreground">{h.snippet}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </PanelBody>
      {kb && <KbViewer id={kb} onClose={() => setKb(null)} />}
    </Panel>
  )
}
