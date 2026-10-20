import { useEffect, useId, useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { ArrowLeft, Ban, Check, FastForward, Play, Save } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { SelectField, TextField } from '@/components/ui/field'
import { ErrorState, InfoNote, RowsSkeleton } from '@/components/ui/states'
import { useConfirm } from '@/components/ui/confirm'
import { useAction } from '@/features/shared/kit'
import { eomApi, eomKeys, periodLabel, type EomPeriodStatus, type EomSettings } from '@/api/eom'
import { formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { tx } from '@/lib/i18n'

/** Başlık ve açıklamalı seçim kartları (tek seçim). */
function Choice<T extends string>({ label, value, onChange, options }: {
  label: string
  value: T
  onChange: (v: T) => void
  options: { value: T; title: string; detail: string }[]
}) {
  return (
    <fieldset className="space-y-2">
      <legend className="mb-1 text-[13px] font-medium">{label}</legend>
      <div role="radiogroup" className="grid gap-2 sm:grid-cols-2">
        {options.map((o) => (
          <button key={o.value} type="button" role="radio" aria-checked={value === o.value} onClick={() => onChange(o.value)}
            className={cn('relative cursor-pointer rounded-xl border p-3 text-left transition-colors hover:border-primary/40',
              value === o.value ? 'border-primary bg-primary/10' : 'border-border')}>
            <span className="block pr-6 text-[13px] font-semibold">{o.title}</span>
            <span className="block text-[12px] text-muted-foreground">{o.detail}</span>
            {value === o.value && <Check className="absolute top-3 right-3 size-4 text-primary" />}
          </button>
        ))}
      </div>
    </fieldset>
  )
}

function Num({ label, value, onChange, min, max, step = 1, hint }: { label: string; value: number; onChange: (n: number) => void; min: number; max: number; step?: number; hint?: string }) {
  return <TextField label={label} hint={hint} type="number" min={min} max={max} step={step} value={String(value)} onChange={(e) => onChange(Number(e.target.value))} />
}

function Section({ title, note, children }: { title: string; note?: string; children: ReactNode }) {
  return (
    <section className="space-y-3 border-t border-border pt-5 first:border-t-0 first:pt-0">
      <div>
        <h3 className="text-[14px] font-semibold">{title}</h3>
        {note && <p className="text-[12.5px] text-muted-foreground">{note}</p>}
      </div>
      {children}
    </section>
  )
}

const STATUS = (): Record<EomPeriodStatus, string> => ({
  Scheduled: tx('Planlandı'), Nominating: tx('Aday gösterme'), Voting: tx('Oylama'), Stage2: tx('Final oylaması'),
  Tie: tx('Beraberlik'), Closed: tx('Sonuçlandı'), Cancelled: tx('İptal'),
})

function Periods() {
  const confirm = useConfirm()
  const q = useQuery({ queryKey: eomKeys.periods, queryFn: ({ signal }) => eomApi.periods(signal) })
  const inv = [eomKeys.all]
  const start = useAction(() => eomApi.startNow(), { success: tx('Oylama başlatıldı; birim üyelerine bildirim gönderildi.'), invalidate: inv })
  const skip = useAction((id: string) => eomApi.skipPhase(id), { success: tx('Aşama bitirildi.'), invalidate: inv })
  const cancel = useAction((id: string) => eomApi.cancel(id), { success: tx('Oylama iptal edildi.'), invalidate: inv })
  const running = (q.data ?? []).some((r) => !['Closed', 'Cancelled', 'Scheduled'].includes(r.period.status))

  return (
    <Panel>
      <PanelHead title={tx('Oylamalar')} note={tx('Takvim dışında hemen başlatabilir, sürmekte olan aşamayı erken bitirebilir ya da iptal edebilirsiniz.')}
        action={
          <Button size="sm" disabled={running || start.isPending} onClick={async () => {
            if (await confirm({ title: tx('Oylama şimdi başlatılsın mı?'), note: tx('Kayıtlı ayarlarla bugünden başlayan bir oylama açılır ve birim üyelerine e-posta gider. Planlanmış dönem varsa iptal edilir.'), action: tx('Başlat') }))
              start.mutate(undefined)
          }}><Play className="size-4" />{tx('Şimdi başlat')}</Button>
        } />
      <PanelBody className="p-0">
        {q.isPending ? <div className="p-5"><RowsSkeleton rows={3} /></div> : !q.data?.length ? (
          <p className="p-5 text-[13px] text-muted-foreground">{tx('Henüz oylama yok.')}</p>
        ) : (
          <ul className="divide-y divide-border">
            {q.data.map(({ period: p, unitCount, tieCount }) => {
              const open = !['Closed', 'Cancelled'].includes(p.status)
              return (
                <li key={p.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
                  <div className="min-w-0 flex-1">
                    <p className="font-medium">{periodLabel(p.key)}</p>
                    <p className="text-[12px] text-muted-foreground">
                      {formatDateTime(p.nominationStartsAt ?? p.votingStartsAt)} – {formatDateTime(p.stage2EndsAt ?? p.stage1EndsAt)}
                      {unitCount > 0 && ` · ${tx('{0} birim', [unitCount])}`}
                    </p>
                  </div>
                  <span className={cn('rounded-full px-2 py-0.5 text-[11.5px]', p.status === 'Tie' ? 'bg-amber-400/15 text-amber-600 dark:text-amber-300' : open ? 'bg-primary/10 text-primary' : 'bg-muted text-muted-foreground')}>
                    {STATUS()[p.status]}{tieCount > 0 && ` · ${tx('{0} beraberlik', [tieCount])}`}
                  </span>
                  <div className="flex gap-1.5">
                    {p.status !== 'Scheduled' && p.status !== 'Cancelled' && (
                      <Button size="sm" variant="ghost" asChild><Link to={`/panel/ayin-calisani/sonuc/${p.id}`}>{p.status === 'Tie' ? tx('Beraberliği çöz') : tx('Sonuçlar')}</Link></Button>
                    )}
                    {open && p.status !== 'Tie' && (
                      <Button size="sm" variant="outline" disabled={skip.isPending} onClick={async () => {
                        if (await confirm({ title: p.status === 'Scheduled' ? tx('Oylama şimdi açılsın mı?') : tx('Bu aşama şimdi bitirilsin mi?'), note: tx('Süre beklenmeden bir sonraki aşamaya geçilir; oylama aşamasıysa oylar sayılır.'), action: tx('Devam') }))
                          skip.mutate(p.id)
                      }}><FastForward className="size-4" />{p.status === 'Scheduled' ? tx('Şimdi aç') : tx('Aşamayı bitir')}</Button>
                    )}
                    {open && (
                      <Button size="sm" variant="ghost" disabled={cancel.isPending} onClick={async () => {
                        if (await confirm({ title: tx('Oylama iptal edilsin mi?'), note: tx('Verilen oylar sayılmaz ve sonuç açıklanmaz.'), action: tx('İptal et') }))
                          cancel.mutate(p.id)
                      }}><Ban className="size-4" />{tx('İptal')}</Button>
                    )}
                  </div>
                </li>
              )
            })}
          </ul>
        )}
      </PanelBody>
    </Panel>
  )
}

export function EomAdminPage() {
  const q = useQuery({ queryKey: eomKeys.settings, queryFn: ({ signal }) => eomApi.settings(signal) })
  const [s, setS] = useState<EomSettings | null>(null)
  const enabledId = useId()
  useEffect(() => { if (q.data) setS(q.data.settings) }, [q.data])
  const save = useAction((v: EomSettings) => eomApi.saveSettings(v), { success: tx('Ayarlar kaydedildi.'), invalidate: [eomKeys.all] })
  const set = <K extends keyof EomSettings>(k: K, v: EomSettings[K]) => setS((o) => (o ? { ...o, [k]: v } : o))

  return (
    <div className="space-y-5">
      <Button variant="ghost" size="sm" className="-ml-2" asChild>
        <Link to="/panel/ayin-calisani"><ArrowLeft className="size-4" />{tx('Ayın çalışanı')}</Link>
      </Button>
      <PageHeader title={tx('Ayın çalışanı · Ayarlar')} eyebrow={[tx('Topluluk')]}
        description={tx('Oylamanın takvimi, kapsamı, gizliliği ve oy katsayıları.')} />
      {!q.data || !s ? (q.isError ? <ErrorState message={q.error instanceof Error ? q.error.message : undefined} onRetry={() => void q.refetch()} /> : <RowsSkeleton />) : (
        <div className="grid gap-5 xl:grid-cols-[1.4fr_1fr]">
          <Panel>
            <PanelHead title={tx('Ayarlar')}
              note={tx('Değişiklikler sıradaki oylamadan geçerlidir; sürmekte olan oylama açıldığı andaki kurallarla tamamlanır.')}
              action={<Button size="sm" disabled={save.isPending} onClick={() => save.mutate(s)}><Save className="size-4" />{tx('Kaydet')}</Button>} />
            <PanelBody className="space-y-5">
              <label htmlFor={enabledId} className="flex cursor-pointer items-start gap-3 rounded-xl border border-border p-3">
                <Checkbox id={enabledId} checked={s.enabled} onCheckedChange={(v) => set('enabled', v === true)} className="mt-0.5" />
                <span>
                  <span className="block text-[13.5px] font-semibold">{tx('Ayın çalışanı açık')}</span>
                  <span className="block text-[12.5px] text-muted-foreground">{tx('Kapalıyken menüde, ana sayfada ve e-postalarda hiçbir şey görünmez.')}</span>
                </span>
              </label>

              <Section title={tx('Oylama düzeyi')}>
                <Choice value={s.scope} onChange={(v) => set('scope', v)} label={tx('Her departman kendi ayın çalışanını seçer')} options={[
                  { value: 'TopLevel', title: tx('Ana departmanlar'), detail: tx('Ana departman alt departmanlarıyla birlikte tek oylama yapar.') },
                  { value: 'EachDepartment', title: tx('Her departman ayrı'), detail: tx('Her departman kendine atanmış kişilerle oylar.') },
                  { value: 'TwoStage', title: tx('İki aşamalı'), detail: tx('Önce alt departmanlar kazananını seçer, sonra ana departman kazananlar arasından seçer.') },
                ]} />
              </Section>

              <Section title={tx('Süreç')}>
                <Choice value={s.process} onChange={(v) => set('process', v)} label={tx('Oylama nasıl başlar')} options={[
                  { value: 'Direct', title: tx('Doğrudan oylama'), detail: tx('Oylama açılınca herkes birimindeki herkese oy verebilir.') },
                  { value: 'Nominate', title: tx('Aday gösterme + oylama'), detail: tx('Önce aday gösterilir; en çok aday gösterilenler oylamaya girer.') },
                ]} />
                {s.process === 'Nominate' && (
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Num label={tx('Aday gösterme süresi (gün)')} value={s.nominationDays} onChange={(v) => set('nominationDays', v)} min={1} max={14} />
                    <Num label={tx('Finalist sayısı')} value={s.finalistCount} onChange={(v) => set('finalistCount', v)} min={2} max={20} hint={tx('Sınırda eşitlik varsa eşit olanların hepsi girer.')} />
                  </div>
                )}
              </Section>

              <Section title={tx('Takvim')} note={tx('Saatler Türkiye saatidir. Açılış günü ayın ilk yarısındaysa önceki ayın çalışanı seçilir.')}>
                <div className="grid gap-3 sm:grid-cols-2">
                  <SelectField label={tx('Sıklık')} value={s.cadence} onChange={(v) => set('cadence', v as EomSettings['cadence'])}
                    options={[{ value: 'Monthly', label: tx('Aylık') }, { value: 'Quarterly', label: tx('Üç aylık') }]} />
                  <Num label={tx('Açılış günü (ayın)')} value={s.startDay} onChange={(v) => set('startDay', v)} min={1} max={28} />
                  <Num label={tx('Açılış saati')} value={s.openHour} onChange={(v) => set('openHour', v)} min={0} max={23} />
                  <Num label={s.scope === 'TwoStage' ? tx('1. aşama süresi (gün)') : tx('Oylama süresi (gün)')} value={s.votingDays} onChange={(v) => set('votingDays', v)} min={1} max={14} />
                  {s.scope === 'TwoStage' && <Num label={tx('Final süresi (gün)')} value={s.stage2Days} onChange={(v) => set('stage2Days', v)} min={1} max={14} />}
                  <Num label={tx('Hatırlatma (gün önce)')} value={s.reminderDaysBefore} onChange={(v) => set('reminderDaysBefore', v)} min={0} max={14} hint={tx('0: yaklaşıyor e-postası gönderilmez.')} />
                </div>
              </Section>

              <Section title={tx('Gizlilik')} note={tx('Seçilen kural, oy verirken kişiye gösterilir ve açılmış bir oylamada değiştirilemez.')}>
                <Choice value={s.visibility} onChange={(v) => set('visibility', v)} label={tx('Oylar ve sonuçlar kime görünür')} options={[
                  { value: 'WinnerOnly', title: tx('Gizli oy, yalnız kazanan'), detail: tx('Kimse kimin kime oy verdiğini görmez. Çalışanlar kazananı, İK puan sıralamasını görür.') },
                  { value: 'RankingPublic', title: tx('Gizli oy, sıralama açık'), detail: tx('Oylar gizli; sonuçta puan sıralaması birimdeki herkese açılır.') },
                  { value: 'HrSeesVotes', title: tx('İK oyları görür'), detail: tx('İK, birim kapanınca kimin kime oy verdiğini görür.') },
                  { value: 'RevealAfter', title: tx('Sonuçta açılır'), detail: tx('Oylama sürerken gizli; sonuç açıklanınca kimin kime oy verdiği açılır.') },
                ]} />
                {s.visibility === 'RevealAfter' && (
                  <SelectField label={tx('Sonuçta oyları kim görür')} value={s.revealTo} onChange={(v) => set('revealTo', v as EomSettings['revealTo'])}
                    options={[{ value: 'Department', label: tx('Birimdeki herkes') }, { value: 'HrAndHead', label: tx('Yalnızca İK ve departman yöneticisi') }]} />
                )}
                <label className="flex cursor-pointer items-center gap-2 text-[13px]">
                  <Checkbox checked={s.allowComment} onCheckedChange={(v) => set('allowComment', v === true)} />
                  {tx('Oy verirken kısa gerekçe yazılabilsin')}
                </label>
              </Section>

              <Section title={tx('Oy katsayıları')} note={tx('Puan, oyların katsayılarının toplamıdır. Birden fazla gruba giren kişide en yüksek katsayı geçerlidir. Yönetici: yönetici rolü, departman başı ya da ekip lideri.')}>
                <div className="grid gap-3 sm:grid-cols-3">
                  <Num label={tx('Çalışan')} value={s.weightEmployee} onChange={(v) => set('weightEmployee', v)} min={0.1} max={10} step={0.5} />
                  <Num label={tx('Yönetici')} value={s.weightManager} onChange={(v) => set('weightManager', v)} min={0.1} max={10} step={0.5} />
                  <Num label={tx('İK')} value={s.weightHr} onChange={(v) => set('weightHr', v)} min={0.1} max={10} step={0.5} />
                </div>
              </Section>

              <Section title={tx('Bekleme süresi')}>
                <Num label={tx('Kazanan kaç dönem aday olamaz')} value={s.cooldownPeriods} onChange={(v) => set('cooldownPeriods', v)} min={0} max={24} hint={tx('0: kazanan bir sonraki dönemde yine aday olabilir.')} />
              </Section>
            </PanelBody>
          </Panel>

          <div className="space-y-5">
            <InfoNote>
              {q.data.active
                ? tx('Sürmekte olan oylama: {0} ({1}).', [periodLabel(q.data.active.key), STATUS()[q.data.active.status]])
                : tx('Kayıtlı ayarlarla sıradaki oylama: {0}, {1} – {2}.', [periodLabel(q.data.next.key), formatDateTime(q.data.next.startsAt), formatDateTime(q.data.next.endsAt)])}
            </InfoNote>
            <Periods />
          </div>
        </div>
      )}
    </div>
  )
}
