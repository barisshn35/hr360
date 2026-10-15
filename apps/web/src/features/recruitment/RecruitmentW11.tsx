/**
 * Dalga 11 (işe alım): çalışan önerisi (73), aday durum bağlantısı paneli (74), ilanın Google for
 * Jobs alanları (75), puan kartı tutarlılığı (77) ve huni analizi (78).
 *
 * KVKK: öneren yalnızca kaba durumu görür; adayın önerilmeyi kabul ettiği onay kutusuyla beyan edilir;
 * analizde 5'ten küçük gruplar gizlenir.
 */
import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { AlertTriangle, BarChart3, Copy, Gift, Link2, Settings2, UserPlus } from 'lucide-react'
import { PageHeader } from '@/components/layout/PageHeader'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Modal } from '@/components/ui/Modal'
import { SelectField, TextAreaField, TextField } from '@/components/ui/Field'
import { StatusBadge, type StatusTone } from '@/components/ui/StatusBadge'
import { EmptyState, InfoNote, RowsSkeleton } from '@/components/ui/States'
import { recruitmentApi, type Application } from '@/api/recruitment'
import {
  recruitmentW11Api,
  type CareerDetails,
  type Consistency,
  type DurationStat,
  type ProgramSettings,
  type Relationship,
  type RewardAction,
  type RewardStatus,
} from '@/api/recruitmentW11'
import { formatDate, formatDateTime, formatMoney, formatNumber } from '@/lib/format'
import { errMsg, isoDate, Metric, useAction } from '@/features/shared/kit'
import { tx, txServer } from '@/lib/i18n'
import { PiiHint } from '@/components/PiiHint'

const RELATIONSHIPS: Record<Relationship, string> = {
  FormerColleague: tx('Eski iş arkadaşı'),
  Friend: tx('Arkadaş'),
  Network: tx('Profesyonel çevre'),
  Other: tx('Diğer'),
}

const COARSE_TONE: Record<string, StatusTone> = { InReview: 'warning', Hired: 'success', Closed: 'neutral' }
const REWARD_TONE: Record<RewardStatus, StatusTone> = {
  None: 'neutral', Waiting: 'info', Eligible: 'warning', Approved: 'success', Paid: 'success', Forfeited: 'danger', NotEligible: 'neutral',
}

/* ================================================================ çalışan: aday öner */

/** /panel/aday-oner — her çalışan. */
export function MyReferralsPage() {
  const q = useQuery({ queryKey: ['recruitment', 'referrals', 'mine'], queryFn: ({ signal }) => recruitmentW11Api.myReferrals(signal) })
  const postings = useQuery({ queryKey: ['recruitment', 'postings', 'Published'], queryFn: ({ signal }) => recruitmentApi.listPostings('Published', signal) })
  const empty = { jobPostingId: '', firstName: '', lastName: '', email: '', phone: '', relationship: '' as Relationship | '', note: '', consent: false }
  const [f, setF] = useState(empty)
  const [error, setError] = useState<string>()
  const s = q.data?.settings
  const refer = useAction(() => recruitmentW11Api.refer({
    jobPostingId: f.jobPostingId, firstName: f.firstName.trim(), lastName: f.lastName.trim(), email: f.email.trim(),
    phone: f.phone.trim() || undefined, relationship: f.relationship || undefined, note: f.note.trim() || undefined, candidateConsent: f.consent,
  }), { invalidate: [['recruitment', 'referrals']], success: tx('Öneriniz alındı; adaya KVKK bilgilendirmesi gönderildi.'), onDone: () => setF(empty) })
  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (!f.jobPostingId) return setError(tx('İlan seçin.'))
    if (f.firstName.trim().length < 2 || f.lastName.trim().length < 2) return setError(tx('Ad ve soyad en az 2 karakter olmalı.'))
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(f.email.trim())) return setError(tx('Geçerli bir e-posta adresi girin.'))
    if (!f.consent) return setError(tx('Adayın önerilmeyi kabul ettiğini onaylamalısınız.'))
    setError(undefined)
    refer.mutate(undefined)
  }
  return (
    <>
      <PageHeader title={tx('Aday öner')} description={tx('Açık pozisyonlara tanıdığınız adayları önerin; süreci genel hatlarıyla izleyin.')} />
      <div className="grid gap-4 xl:grid-cols-[1fr_1.2fr]">
        <Panel>
          <PanelHead title={tx('Yeni öneri')} note={s?.referralRewardAmount ? tx('İşe alınan ve deneme süresini ({0} gün) tamamlayan öneriler için ödül: {1}', [s.referralProbationDays, formatMoney(s.referralRewardAmount, s.referralRewardCurrency)]) : undefined} />
          <PanelBody>
            {s && !s.referralEnabled ? <InfoNote>{tx('Çalışan önerisi programı şu anda kapalı.')}</InfoNote> : (
              <form onSubmit={submit} noValidate className="space-y-3">
                <SelectField label={tx('İlan')} value={f.jobPostingId} onChange={(v) => setF({ ...f, jobPostingId: v })} required
                  options={(postings.data ?? []).filter((p) => p.status === 'Published').map((p) => ({ value: p.id, label: p.title }))}
                  hint={postings.data && !postings.data.some((p) => p.status === 'Published') ? tx('Şu anda yayında ilan yok.') : undefined} />
                <div className="grid gap-3 sm:grid-cols-2">
                  <TextField label={tx('Adayın adı')} required maxLength={100} value={f.firstName} onChange={(e) => setF({ ...f, firstName: e.target.value })} />
                  <TextField label={tx('Adayın soyadı')} required maxLength={100} value={f.lastName} onChange={(e) => setF({ ...f, lastName: e.target.value })} />
                </div>
                <div className="grid gap-3 sm:grid-cols-2">
                  <TextField label={tx('E-posta')} type="email" required maxLength={200} value={f.email} onChange={(e) => setF({ ...f, email: e.target.value })} />
                  <TextField label={tx('Telefon (isteğe bağlı)')} type="tel" maxLength={30} value={f.phone} onChange={(e) => setF({ ...f, phone: e.target.value })} />
                </div>
                <SelectField label={tx('Adayı nereden tanıyorsunuz? (isteğe bağlı)')} value={f.relationship} onChange={(v) => setF({ ...f, relationship: v as Relationship })}
                  options={(Object.keys(RELATIONSHIPS) as Relationship[]).map((r) => ({ value: r, label: RELATIONSHIPS[r] }))} />
                <TextAreaField label={tx('Neden öneriyorsunuz? (isteğe bağlı)')} rows={3} maxLength={1000} value={f.note} onChange={(e) => setF({ ...f, note: e.target.value })}
                  hint={tx('Yalnızca işle ilgili yetkinlikler; sağlık, din, aile durumu gibi bilgiler yazmayın.')} />
                <PiiHint text={f.note} />
                <label className="flex items-start gap-2.5 rounded-xl border border-dashed border-border p-3 text-[13px]">
                  <Checkbox className="mt-0.5" checked={f.consent} onCheckedChange={(v) => setF({ ...f, consent: v === true })} />
                  <span>
                    <strong>{tx('Adayın onayı:')}</strong>{' '}
                    {tx('Adayın bu pozisyona önerilmeyi ve iletişim bilgilerinin İnsan Kaynakları ile paylaşılmasını kabul ettiğini beyan ederim.')}
                    <span className="block text-[12px] text-muted-foreground">{tx('Adaya KVKK aydınlatma e-postası gönderilir; adınız e-postada yer almaz.')}</span>
                  </span>
                </label>
                {error && <p role="alert" className="text-[13px] text-destructive">{error}</p>}
                <Button type="submit" disabled={refer.isPending}><UserPlus className="size-4" /> {tx('Öner')}</Button>
              </form>
            )}
          </PanelBody>
        </Panel>
        <Panel>
          <PanelHead title={tx('Önerilerim')} note={tx('Gizlilik gereği yalnızca genel durum gösterilir.')} />
          <PanelBody className="p-0">
            {q.isPending ? <div className="p-4"><RowsSkeleton rows={3} /></div> : q.isError ? <p role="alert" className="p-4 text-[13px] text-destructive">{errMsg(q.error)}</p>
              : !q.data.items.length ? <EmptyState icon={UserPlus} title={tx('Henüz öneriniz yok')} detail={tx('Soldaki formdan bir aday önerebilirsiniz.')} /> : (
                <ul className="divide-y divide-border">
                  {q.data.items.map((r) => (
                    <li key={r.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
                      <div className="min-w-0 flex-1">
                        <p className="font-medium">{r.candidateName ?? '—'} · {r.posting ?? '—'}</p>
                        <p className="text-[12px] text-muted-foreground">{formatDate(r.createdAt)}{r.rewardStatus !== 'None' ? ` · ${tx('Ödül')}: ${txServer(r.rewardLabel)}` : ''}</p>
                      </div>
                      <StatusBadge tone={COARSE_TONE[r.status] ?? 'neutral'}>{txServer(r.statusLabel)}</StatusBadge>
                    </li>
                  ))}
                </ul>
              )}
          </PanelBody>
        </Panel>
      </div>
    </>
  )
}

/* ================================================================ İK: öneri panosu */

function SettingsModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const q = useQuery({ queryKey: ['recruitment', 'program-settings'], queryFn: ({ signal }) => recruitmentW11Api.settings(signal), enabled: open })
  const [s, setS] = useState<ProgramSettings | null>(null)
  useEffect(() => { if (q.data) setS(q.data) }, [q.data])
  const save = useAction(() => recruitmentW11Api.saveSettings(s!), { invalidate: [['recruitment']], success: tx('Ayarlar kaydedildi'), onDone: onClose })
  if (!open) return null
  return (
    <Modal open onClose={onClose} title={tx('İşe alım program ayarları')}
      footer={<><Button variant="outline" onClick={onClose}>{tx('Vazgeç')}</Button><Button disabled={!s || save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button></>}>
      {!s ? <RowsSkeleton rows={3} /> : (
        <div className="space-y-3 text-[13px]">
          <label className="flex items-center gap-2"><Checkbox checked={s.referralEnabled} onCheckedChange={(v) => setS({ ...s, referralEnabled: v === true })} />{tx('Çalışan önerisi programı açık')}</label>
          <div className="grid gap-3 sm:grid-cols-3">
            <TextField label={tx('Ödül tutarı')} type="number" min={0} value={s.referralRewardAmount ?? ''} onChange={(e) => setS({ ...s, referralRewardAmount: e.target.value === '' ? null : Number(e.target.value) })} />
            <SelectField label={tx('Para birimi')} value={s.referralRewardCurrency} onChange={(v) => setS({ ...s, referralRewardCurrency: v })}
              options={['TRY', 'USD', 'EUR', 'GBP'].map((c) => ({ value: c, label: c }))} />
            <TextField label={tx('Deneme süresi (gün)')} type="number" min={0} max={365} value={s.referralProbationDays} onChange={(e) => setS({ ...s, referralProbationDays: Number(e.target.value) })} />
          </div>
          <TextAreaField label={tx('Ödül koşulları (isteğe bağlı)')} rows={2} maxLength={500} value={s.referralRewardNote ?? ''} onChange={(e) => setS({ ...s, referralRewardNote: e.target.value })} />
          <label className="flex items-start gap-2">
            <Checkbox className="mt-0.5" checked={s.publishSalaryInJobPostings} onCheckedChange={(v) => setS({ ...s, publishSalaryInJobPostings: v === true })} />
            <span>{tx('İlan ücret aralığını kariyer sayfasında ve Google for Jobs verisinde yayımla')}<span className="block text-[12px] text-muted-foreground">{tx('Kapalıyken ücret yalnızca İK ekranlarında görünür.')}</span></span>
          </label>
        </div>
      )}
    </Modal>
  )
}

/** /panel/ise-alim/oneriler — İK. */
export function ReferralsAdminPage() {
  const [filter, setFilter] = useState<RewardStatus | ''>('')
  const q = useQuery({ queryKey: ['recruitment', 'referrals', 'admin', filter], queryFn: ({ signal }) => recruitmentW11Api.referrals(filter || undefined, signal) })
  const [settings, setSettings] = useState(false)
  const [decide, setDecide] = useState<{ id: string; action: RewardAction; amount: string; note: string } | null>(null)
  const act = useAction(() => recruitmentW11Api.decideReward(decide!.id, {
    action: decide!.action, amount: decide!.amount ? Number(decide!.amount) : undefined, note: decide!.note.trim() || undefined,
  }), { invalidate: [['recruitment', 'referrals']], success: tx('Ödül kararı kaydedildi'), onDone: () => setDecide(null) })
  const evaluate = useAction(() => recruitmentW11Api.evaluateRewards(), { invalidate: [['recruitment', 'referrals']], success: (r) => tx('{0} öneri güncellendi', [r.changed]) })
  const sum = q.data?.summary
  const ACTIONS: Record<RewardAction, string> = { approve: tx('Onayla'), pay: tx('Ödendi'), forfeit: tx('Düşür'), 'not-eligible': tx('Uygun değil') }
  const allowed = (s: RewardStatus): RewardAction[] =>
    s === 'Eligible' ? ['approve', 'forfeit', 'not-eligible'] : s === 'Approved' ? ['pay', 'forfeit'] : s === 'Waiting' ? ['forfeit', 'not-eligible'] : []
  return (
    <>
      <PageHeader title={tx('Çalışan önerileri')} description={tx('Öneri programı, ödül hak edişi ve onaylar. Ödül kararı her zaman İK onayıyla verilir.')}
        actions={<div className="flex gap-2">
          <Button variant="outline" size="sm" disabled={evaluate.isPending} onClick={() => evaluate.mutate(undefined)}><Gift className="size-4" /> {tx('Hak edişi şimdi hesapla')}</Button>
          <Button variant="outline" size="sm" onClick={() => setSettings(true)}><Settings2 className="size-4" /> {tx('Ayarlar')}</Button>
        </div>} />
      {sum && (
        <div className="mb-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-5">
          <Metric label={tx('Toplam öneri')} value={formatNumber(sum.total)} hint={tx('{0} öneren', [sum.referrers])} />
          <Metric label={tx('Süreçte')} value={formatNumber(sum.inReview)} />
          <Metric label={tx('İşe alınan')} value={formatNumber(sum.hired)} hint={sum.hireRate != null ? tx('%{0} dönüşüm', [sum.hireRate]) : undefined} tone="good" />
          <Metric label={tx('Onay bekleyen ödül')} value={formatNumber(sum.awaitingApproval)} hint={tx('{0} deneme süresinde', [sum.waiting])} tone={sum.awaitingApproval ? 'warn' : undefined} />
          <Metric label={tx('Ödenen toplam')} value={formatMoney(sum.paidTotal)} />
        </div>
      )}
      <Panel>
        <PanelHead title={tx('Öneriler')} action={
          <div className="w-52"><SelectField label={tx('Ödül durumu')} value={filter} onChange={(v) => setFilter(v as RewardStatus)}
            options={(['Waiting', 'Eligible', 'Approved', 'Paid', 'Forfeited', 'NotEligible'] as RewardStatus[]).map((r) => ({ value: r, label: r }))} placeholder={tx('Tümü')} /></div>} />
        <PanelBody className="p-0">
          {q.isPending ? <div className="p-4"><RowsSkeleton rows={4} /></div> : q.isError ? <p role="alert" className="p-4 text-[13px] text-destructive">{errMsg(q.error)}</p>
            : !q.data.items.length ? <EmptyState icon={UserPlus} title={tx('Öneri yok')} detail={tx('Çalışanlar "Aday öner" sayfasından öneri yapar.')} /> : (
              <ul className="divide-y divide-border">
                {q.data.items.map((r) => (
                  <li key={r.id} className="flex flex-wrap items-center gap-3 px-5 py-3 text-[13px]">
                    <div className="min-w-0 flex-1">
                      <p className="font-medium">{r.candidateName ?? '—'} · {r.posting ?? '—'}</p>
                      <p className="text-[12px] text-muted-foreground">
                        {tx('Öneren: {0}', [r.referrer ?? '—'])} · {formatDate(r.createdAt)}{r.relationship ? ` · ${RELATIONSHIPS[r.relationship]}` : ''}
                        {r.noticeSentAt ? ` · ${tx('KVKK bilgilendirmesi gönderildi')}` : ''}
                      </p>
                      {r.rewardEligibleAt && <p className="text-[12px] text-muted-foreground">{tx('Hak ediş tarihi: {0}', [formatDate(r.rewardEligibleAt)])}{r.rewardAmount != null ? ` · ${formatMoney(r.rewardAmount, r.rewardCurrency ?? 'TRY')}` : ''}</p>}
                      {r.note && <p className="mt-1 border-l-2 border-border pl-2 text-[12.5px]">{r.note}</p>}
                    </div>
                    <StatusBadge tone={COARSE_TONE[r.status] ?? 'neutral'}>{txServer(r.statusLabel)}</StatusBadge>
                    <StatusBadge tone={REWARD_TONE[r.rewardStatus]}>{txServer(r.rewardLabel)}</StatusBadge>
                    {allowed(r.rewardStatus).map((a) => (
                      <Button key={a} size="sm" variant={a === 'approve' || a === 'pay' ? 'default' : 'ghost'}
                        onClick={() => setDecide({ id: r.id, action: a, amount: r.rewardAmount?.toString() ?? '', note: '' })}>{ACTIONS[a]}</Button>
                    ))}
                  </li>
                ))}
              </ul>
            )}
        </PanelBody>
      </Panel>
      <SettingsModal open={settings} onClose={() => setSettings(false)} />
      {decide && (
        <Modal open onClose={() => setDecide(null)} title={tx('Ödül kararı: {0}', [ACTIONS[decide.action]])}
          footer={<><Button variant="outline" onClick={() => setDecide(null)}>{tx('Vazgeç')}</Button><Button disabled={act.isPending} onClick={() => act.mutate(undefined)}>{tx('Kaydet')}</Button></>}>
          <div className="space-y-3">
            {decide.action === 'approve' && <TextField label={tx('Ödül tutarı')} type="number" min={0} value={decide.amount} onChange={(e) => setDecide({ ...decide, amount: e.target.value })} />}
            <TextAreaField label={tx('Not (isteğe bağlı)')} rows={2} maxLength={500} value={decide.note} onChange={(e) => setDecide({ ...decide, note: e.target.value })} />
            <p className="text-[12px] text-muted-foreground">{tx('Karar denetim kaydına yazılır; onay ve ödeme önerene bildirilir.')}</p>
          </div>
        </Modal>
      )}
    </>
  )
}

/* ================================================================ İK: huni analizi */

const STAGE_LABELS: Record<string, string> = {
  Applied: tx('Başvuru'), Screening: tx('Ön eleme'), Interview: tx('Mülakat'), Offer: tx('Teklif'), Hired: tx('İşe alındı'),
}
const SOURCE_LABELS: Record<string, string> = { Career: tx('Kariyer sayfası'), Referral: tx('Çalışan önerisi'), Other: tx('Diğer') }

function Days({ d, k }: { d: DurationStat; k: number }) {
  if (d.suppressed) return <span className="text-muted-foreground" title={tx('{0} kişiden az grup gizlenir', [k])}>{tx('<{0}', [k])}</span>
  if (d.medianDays == null) return <span className="text-muted-foreground">—</span>
  return <span className="tabular">{tx('{0} gün', [d.medianDays])}</span>
}

/** /panel/ise-alim/analiz — İK. */
export function RecruitmentAnalyticsPage() {
  const [from, setFrom] = useState(() => isoDate(new Date(Date.now() - 365 * 864e5)))
  const [to, setTo] = useState(() => isoDate())
  const [postingId, setPostingId] = useState('')
  const postings = useQuery({ queryKey: ['recruitment', 'postings', 'all'], queryFn: ({ signal }) => recruitmentApi.listPostings(undefined, signal) })
  const q = useQuery({
    queryKey: ['recruitment', 'funnel', from, to, postingId],
    queryFn: ({ signal }) => recruitmentW11Api.funnel({ from: `${from}T00:00:00Z`, to: `${to}T23:59:59Z`, jobPostingId: postingId || undefined }, signal),
  })
  const d = q.data
  const top = Math.max(1, d?.stages[0]?.reached ?? 1)
  return (
    <>
      <PageHeader title={tx('İşe alım analizi')} description={tx('Huni, aşama süreleri, kaynak dönüşümü ve teklif kabulü. 5 kişiden küçük gruplar gizlenir.')} />
      <Panel className="mb-4">
        <PanelBody className="grid gap-3 sm:grid-cols-3">
          <TextField label={tx('Başlangıç')} type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
          <TextField label={tx('Bitiş')} type="date" value={to} onChange={(e) => setTo(e.target.value)} />
          <SelectField label={tx('İlan')} value={postingId} onChange={setPostingId} placeholder={tx('Tüm ilanlar')}
            options={(postings.data ?? []).map((p) => ({ value: p.id, label: p.title }))} />
        </PanelBody>
      </Panel>
      {q.isPending ? <RowsSkeleton rows={5} /> : q.isError ? <p role="alert" className="text-[13px] text-destructive">{errMsg(q.error)}</p> : !d || d.applications === 0
        ? <EmptyState icon={BarChart3} title={tx('Bu aralıkta başvuru yok')} detail={tx('Tarih aralığını genişletin.')} /> : (
          <div className="space-y-4">
            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
              <Metric label={tx('Başvuru')} value={formatNumber(d.applications)} />
              <Metric label={tx('İşe alım süresi (medyan)')} value={<Days d={d.timeToHire} k={d.minGroup} />} hint={d.timeToHire.p75Days != null ? tx('%75: {0} gün', [d.timeToHire.p75Days]) : undefined} />
              <Metric label={tx('Teklif kabul oranı')} value={d.offers.suppressed ? tx('<{0}', [d.minGroup]) : d.offers.acceptanceRate != null ? `%${d.offers.acceptanceRate}` : '—'} hint={tx('{0} teklif gönderildi', [d.offers.sent])} />
              <Metric label={tx('İşe alınan')} value={formatNumber(d.stages.find((s) => s.stage === 'Hired')?.reached ?? 0)} />
            </div>
            <Panel>
              <PanelHead title={tx('Huni ve aşamada geçen süre')} note={tx('Aşamaya ulaşan başvuru sayısı, bir önceki aşamadan geçiş oranı ve aşamada geçen medyan süre.')} />
              <PanelBody>
                <table className="w-full text-[13px]">
                  <thead><tr className="text-left text-[12px] text-muted-foreground"><th className="py-1">{tx('Aşama')}</th><th>{tx('Ulaşan')}</th><th className="w-1/3" /><th>{tx('Geçiş')}</th><th>{tx('Aşamada (medyan)')}</th></tr></thead>
                  <tbody>
                    {d.stages.map((s) => (
                      <tr key={s.stage} className="border-t border-border">
                        <td className="py-2">{STAGE_LABELS[s.stage] ?? s.stage}</td>
                        <td className="tabular">{formatNumber(s.reached)}</td>
                        <td><div className="h-2 rounded-full bg-muted"><div className="h-2 rounded-full bg-primary" style={{ width: `${Math.round((s.reached / top) * 100)}%` }} /></div></td>
                        <td className="tabular">{s.conversionFromPrevious != null ? `%${s.conversionFromPrevious}` : '—'}</td>
                        <td>{s.stage === 'Hired' ? '—' : <Days d={s.timeInStage} k={d.minGroup} />}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </PanelBody>
            </Panel>
            <Panel>
              <PanelHead title={tx('Kaynak dönüşümü')} />
              <PanelBody>
                <table className="w-full text-[13px]">
                  <thead><tr className="text-left text-[12px] text-muted-foreground"><th className="py-1">{tx('Kaynak')}</th><th>{tx('Başvuru')}</th><th>{tx('İşe alınan')}</th><th>{tx('Oran')}</th></tr></thead>
                  <tbody>
                    {d.sources.map((s) => (
                      <tr key={s.source} className="border-t border-border">
                        <td className="py-2">{SOURCE_LABELS[s.source] ?? s.source}</td>
                        {s.suppressed ? <td colSpan={3} className="text-muted-foreground">{tx('{0} kişiden az — gizlendi', [d.minGroup])}</td> : <>
                          <td className="tabular">{formatNumber(s.applications ?? 0)}</td>
                          <td className="tabular">{formatNumber(s.hired ?? 0)}</td>
                          <td className="tabular">%{s.hireRate}</td>
                        </>}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </PanelBody>
            </Panel>
          </div>
        )}
    </>
  )
}

/* ================================================================ ilan ayrıntısı panelleri */

/** İlan sayfasında: başvuru için oturumsuz durum bağlantısı üret / iptal et. */
export function StatusLinksPanel({ applications, names }: { applications: Application[]; names: Map<string, string> }) {
  const [appId, setAppId] = useState('')
  const [sendEmail, setSendEmail] = useState(true)
  const [days, setDays] = useState('90')
  const [issued, setIssued] = useState<string | null>(null)
  const links = useQuery({ queryKey: ['recruitment', 'status-links', appId], queryFn: ({ signal }) => recruitmentW11Api.statusLinks(appId, signal), enabled: !!appId })
  const issue = useAction(() => recruitmentW11Api.issueStatusLink(appId, { days: Number(days) || undefined, sendEmail }), {
    invalidate: [['recruitment', 'status-links', appId]],
    success: (r) => (r.emailed ? tx('Bağlantı üretildi ve adaya e-postayla gönderildi') : tx('Bağlantı üretildi')),
    onDone: (r) => setIssued(`${window.location.origin}${r.path}`),
  })
  const revoke = useAction(() => recruitmentW11Api.revokeStatusLinks(appId), { invalidate: [['recruitment', 'status-links', appId]], success: (r) => tx('{0} bağlantı iptal edildi', [r.revoked]) })
  const options = useMemo(() => applications.map((a) => ({ value: a.id, label: names.get(a.id) || a.id.slice(0, 8) })), [applications, names])
  const REVOKED_BY: Record<string, string> = { Hr: tx('İK iptal etti'), Candidate: tx('Aday iptal etti'), Reissued: tx('Yenisiyle değişti') }
  return (
    <Panel>
      <PanelHead title={tx('Aday durum bağlantısı')} note={tx('Oturumsuz; yalnızca genel aşama ve sonraki adım görünür. İptal edilebilir, süresi dolar.')} />
      <PanelBody className="space-y-3">
        <SelectField label={tx('Başvuru')} value={appId} onChange={(v) => { setAppId(v); setIssued(null) }} options={options} />
        {appId && <>
          <div className="flex flex-wrap items-end gap-3">
            <div className="w-28"><TextField label={tx('Geçerlilik (gün)')} type="number" min={1} max={180} value={days} onChange={(e) => setDays(e.target.value)} /></div>
            <label className="flex items-center gap-2 pb-2 text-[13px]"><Checkbox checked={sendEmail} onCheckedChange={(v) => setSendEmail(v === true)} />{tx('Adaya e-postayla gönder')}</label>
            <Button size="sm" disabled={issue.isPending} onClick={() => issue.mutate(undefined)}><Link2 className="size-4" /> {tx('Bağlantı üret')}</Button>
            <Button size="sm" variant="ghost" disabled={revoke.isPending} onClick={() => revoke.mutate(undefined)}>{tx('Tümünü iptal et')}</Button>
          </div>
          {issued && (
            <div className="flex gap-2">
              <input readOnly value={issued} aria-label={tx('Durum bağlantısı')} className="h-9 flex-1 rounded-lg border border-input bg-muted/40 px-2 text-[12.5px]" />
              <Button variant="outline" size="sm" onClick={() => void navigator.clipboard?.writeText(issued)}><Copy className="size-4" /> {tx('Kopyala')}</Button>
            </div>
          )}
          {issued && <p className="text-[12px] text-muted-foreground">{tx('Bağlantı yalnızca şimdi gösterilir; sunucuda yalnızca özeti tutulur.')}</p>}
          <ul className="space-y-1 text-[12.5px]">
            {(links.data ?? []).map((l) => (
              <li key={l.id} className="flex flex-wrap items-center gap-2">
                <StatusBadge tone={l.active ? 'success' : 'neutral'}>{l.active ? tx('Etkin') : l.revokedBy ? REVOKED_BY[l.revokedBy] : tx('Süresi doldu')}</StatusBadge>
                <span className="text-muted-foreground">{tx('{0} · {1} tarihine kadar · {2} görüntüleme', [formatDateTime(l.createdAt), formatDate(l.expiresAt), l.viewCount])}</span>
              </li>
            ))}
          </ul>
        </>}
      </PanelBody>
    </Panel>
  )
}

/** İlan sayfasında (İK): Google for Jobs alanları. */
export function CareerDetailsPanel({ posting }: { posting: { id: string } & Partial<CareerDetails> }) {
  const [f, setF] = useState<CareerDetails>({ remoteAllowed: false })
  useEffect(() => {
    setF({
      location: posting.location ?? '', region: posting.region ?? '', country: posting.country ?? 'TR', remoteAllowed: posting.remoteAllowed ?? false,
      validThrough: posting.validThrough ? posting.validThrough.slice(0, 10) : '', salaryMin: posting.salaryMin ?? null, salaryMax: posting.salaryMax ?? null,
      salaryCurrency: posting.salaryCurrency ?? 'TRY', salaryPeriod: posting.salaryPeriod ?? 'MONTH',
    })
  }, [posting])
  const save = useAction(() => recruitmentW11Api.saveCareerDetails(posting.id, {
    ...f, validThrough: f.validThrough ? `${f.validThrough}T23:59:59Z` : null,
    salaryMin: f.salaryMin || null, salaryMax: f.salaryMax || null,
  }), { invalidate: [['recruitment']], success: tx('Kariyer sayfası alanları kaydedildi') })
  const num = (v: string) => (v === '' ? null : Number(v))
  return (
    <Panel>
      <PanelHead title={tx('Kariyer sayfası ve Google for Jobs')} note={tx('Konum, son başvuru tarihi ve ücret aralığı. Ücret yalnızca program ayarlarında açıksa yayımlanır.')} />
      <PanelBody className="space-y-3">
        <div className="grid gap-3 sm:grid-cols-3">
          <TextField label={tx('Şehir')} maxLength={120} value={f.location ?? ''} onChange={(e) => setF({ ...f, location: e.target.value })} />
          <TextField label={tx('Bölge')} maxLength={120} value={f.region ?? ''} onChange={(e) => setF({ ...f, region: e.target.value })} />
          <TextField label={tx('Ülke kodu')} maxLength={2} value={f.country ?? ''} onChange={(e) => setF({ ...f, country: e.target.value.toUpperCase() })} />
        </div>
        <div className="grid gap-3 sm:grid-cols-2">
          <TextField label={tx('Son başvuru tarihi')} type="date" value={f.validThrough ?? ''} onChange={(e) => setF({ ...f, validThrough: e.target.value })} />
          <label className="flex items-center gap-2 pt-6 text-[13px]"><Checkbox checked={f.remoteAllowed} onCheckedChange={(v) => setF({ ...f, remoteAllowed: v === true })} />{tx('Uzaktan çalışılabilir')}</label>
        </div>
        <div className="grid gap-3 sm:grid-cols-4">
          <TextField label={tx('Ücret alt sınırı')} type="number" min={0} value={f.salaryMin ?? ''} onChange={(e) => setF({ ...f, salaryMin: num(e.target.value) })} />
          <TextField label={tx('Ücret üst sınırı')} type="number" min={0} value={f.salaryMax ?? ''} onChange={(e) => setF({ ...f, salaryMax: num(e.target.value) })} />
          <SelectField label={tx('Para birimi')} value={f.salaryCurrency ?? 'TRY'} onChange={(v) => setF({ ...f, salaryCurrency: v })} options={['TRY', 'USD', 'EUR', 'GBP'].map((c) => ({ value: c, label: c }))} />
          <SelectField label={tx('Dönem')} value={f.salaryPeriod ?? 'MONTH'} onChange={(v) => setF({ ...f, salaryPeriod: v })}
            options={[{ value: 'MONTH', label: tx('Aylık') }, { value: 'YEAR', label: tx('Yıllık') }, { value: 'HOUR', label: tx('Saatlik') }]} />
        </div>
        <Button size="sm" disabled={save.isPending} onClick={() => save.mutate(undefined)}>{tx('Kaydet')}</Button>
      </PanelBody>
    </Panel>
  )
}

/* ================================================================ puan kartı tutarlılığı */

/** Değerlendiriciler arası tutarlılık (ölçüt başına std/açıklık; yüksek görüş ayrılığı işaretlenir). */
export function ConsistencyView({ c }: { c: Consistency | null | undefined }) {
  if (!c || c.raters < 2) return null
  return (
    <div className="rounded-xl border border-border p-3 text-[12.5px]">
      <p className="mb-2 flex items-center gap-1.5 font-medium">
        {c.highDisagreement && <AlertTriangle className="size-4 text-[hsl(var(--warning))]" />}
        {c.highDisagreement ? tx('Görüşmeciler arasında yüksek görüş ayrılığı var — kararı birlikte değerlendirin.') : tx('Görüşmeciler büyük ölçüde tutarlı.')}
      </p>
      <table className="w-full">
        <thead><tr className="text-left text-muted-foreground"><th className="py-0.5">{tx('Ölçüt')}</th><th>{tx('Ortalama')}</th><th>{tx('Std. sapma')}</th><th>{tx('Açıklık')}</th></tr></thead>
        <tbody>
          {c.criteria.filter((x) => x.count > 0).map((x) => (
            <tr key={x.key} className={x.highDisagreement ? 'text-[hsl(var(--warning))]' : ''}>
              <td className="py-0.5">{x.label}{x.highDisagreement ? ' ⚠' : ''}</td>
              <td className="tabular">{x.mean?.toFixed(2)}</td>
              <td className="tabular">{x.std?.toFixed(2)}</td>
              <td className="tabular">{x.spread}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {c.recommendationSplit && <p className="mt-2 text-[hsl(var(--warning))]">{tx('Önerilerde hem olumlu hem olumsuz görüş var.')}</p>}
    </div>
  )
}
