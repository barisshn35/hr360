import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Plus, Sparkles } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { TextAreaField } from '@/components/ui/Field'
import { mlInsightsApi, type SkillExtract } from '@/api/mlInsights'
import { learningContentApi } from '@/api/learningContent'
import { newSuggestions } from '@/lib/mlInsights'
import { tx } from '@/lib/i18n'
import { errMsg } from './kit'

/**
 * Beceri önerisi (ML dalgası 2, madde 47): metinden sözlük + n-gram eşleşmesiyle beceriler ve şirketin
 * yetkinlik kataloğuyla eşleşmeler. Hiçbir şey OTOMATİK eklenmez: kullanıcı öneriye tıklayınca listeye
 * girer, kaydetmek yine kullanıcının "Kaydet" adımıdır. Yapıştırılan metin saklanmaz.
 */
export function SkillSuggest({ existing, onAdd, text: givenText, paste = true }: {
  existing: string[]
  onAdd: (skill: string) => void
  /** Hazır metin (ör. CV ayrıştırma sonucu); yoksa kullanıcı yapıştırır. */
  text?: string
  paste?: boolean
}) {
  const [text, setText] = useState('')
  const [result, setResult] = useState<SkillExtract | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const catalog = useQuery({ queryKey: ['learning', 'competencies', 'catalog'], queryFn: ({ signal }) => learningContentApi.competencies(signal), staleTime: 5 * 60_000, retry: false })
  const source = (givenText ?? '') + (text ? `\n${text}` : '')

  async function run() {
    setBusy(true); setError(null)
    try {
      const comps = (catalog.data ?? []).filter((c) => c.isActive).map((c) => ({ id: c.id, name: c.name, category: c.category }))
      setResult(await mlInsightsApi.extractSkills({ text: source, catalog: comps, known: existing }))
    } catch (e) {
      setError(errMsg(e, tx('Beceri önerisi alınamadı.')))
    } finally {
      setBusy(false)
    }
  }

  const suggestions = result ? newSuggestions([...result.catalog_matches.map((m) => m.name), ...result.skills.map((s) => s.label)], existing) : []
  return (
    <div className="space-y-2 rounded-xl border border-dashed border-border p-3">
      {paste && (
        <TextAreaField label={tx('Özgeçmiş ya da deneyim metni (saklanmaz)')} rows={3} maxLength={20000} value={text} onChange={(e) => setText(e.target.value)}
          placeholder={tx('CV\'nizden ya da LinkedIn özetinizden bir bölüm yapıştırın')} />
      )}
      <div className="flex flex-wrap items-center gap-2">
        <Button type="button" size="sm" variant="outline" disabled={busy || source.trim().length < 3} onClick={() => void run()}>
          <Sparkles className="size-4" /> {tx('Beceri öner')}
        </Button>
        <span className="text-[11.5px] text-muted-foreground">{tx('Öneriler yalnızca siz eklerseniz listeye girer.')}</span>
      </div>
      {error && <p className="text-[12.5px] text-destructive">{error}</p>}
      {result && (suggestions.length === 0 ? (
        <p className="text-[12.5px] text-muted-foreground">{tx('Yeni bir beceri önerisi yok.')}</p>
      ) : (
        <div className="flex flex-wrap gap-1.5">
          {suggestions.map((s) => {
            const m = result.catalog_matches.find((x) => x.name === s)
            return (
              <button key={s} type="button" onClick={() => onAdd(s)}
                title={m ? tx('Yetkinlik kataloğu · kanıt: {0}', [m.evidence]) : tx('Metinde geçiyor')}
                className="inline-flex items-center gap-1 rounded-full border border-primary/40 bg-primary/5 px-2.5 py-0.5 text-[12px] text-primary hover:bg-primary/10">
                <Plus className="size-3" /> {s}{m ? ' ★' : ''}
              </button>
            )
          })}
        </div>
      ))}
      {result && suggestions.length > 0 && <p className="text-[11px] text-muted-foreground">{tx('★ şirketin yetkinlik kataloğuyla eşleşenler.')}</p>}
    </div>
  )
}
