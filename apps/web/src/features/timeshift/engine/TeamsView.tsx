/**
 * Vardiya ekipleri: ekip oluştur (desen + başlangıç + opsiyonel departman),
 * üye ekle/çıkar, her üyeye sıra (rank) ve etiket ver.
 *
 * Departman kısıtı backend'de zorlanmıyor; burada uygulanıyor: ekip bir
 * departmana bağlıysa üye listesinde yalnızca o departmanın (ve alt
 * departmanlarının) güncel çalışanları çıkar.
 */

import { useMemo, useState } from 'react'
import { CalendarRange, LoaderCircle, Pencil, Plus, Trash2, TriangleAlert, UserMinus, UserPlus, UsersRound } from 'lucide-react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import {
  useAddShiftTeamMember,
  useCreateShiftTeam,
  useRemoveShiftTeamMember,
  useShiftPatterns,
  useShiftTeams,
  qke,
} from '@/api/queries-shift-engine'
import { timeshiftApi, type ShiftTeam, type ShiftTeamMember } from '@/api/timeshift'
import { useConfirm } from '@/components/ui/Confirm'
import { useAuth } from '@/auth/useAuth'
import { Modal } from '@/components/ui/Modal'
import { Panel, PanelBody, PanelHead } from '@/components/ui/Panel'
import { EmptyState, ErrorState, RowsSkeleton } from '@/components/ui/States'
import { SelectField, TextField } from '@/components/ui/Field'
import { Button } from '@/components/ui/button'
import { useToast } from '@/components/ui/Toast'
import { formatDate, formatNumber } from '@/lib/format'
import { cn } from '@/lib/utils'
import { errorText } from '@/features/performance/components/controls'
import { PersonAvatar, PersonPicker } from '@/features/performance/components/people'
import { useDepartments, usePeople, type DeptNode } from '@/features/performance/hooks'
import {
  DAY_STYLE,
  DayBlock,
  LaborWarnings,
  PatternStrip,
  addDays,
  dateRange,
  describeDays,
  patternIndexAt,
  sortedDays,
  timeRange,
  todayIso,
  weekdayShort,
} from './shared'
import { tx, appLocale } from '@/lib/i18n'

const NO_DEPT = '__none__'
const ALL = '__all__'

const TAG_SUGGESTIONS = [tx('Ekip Lideri'), tx('Vardiya Amiri'), tx('Kıdemli Operatör'), tx('Operatör'), tx('Stajyer')]

/** Departman ve tüm alt departmanlarının kimlikleri. */
function subtreeIds(list: DeptNode[], rootId: string): Set<string> {
  const node = list.find((d) => d.id === rootId)
  const out = new Set<string>([rootId])
  const walk = (n: DeptNode) => n.children.forEach((c) => (out.add(c.id), walk(c)))
  if (node) walk(node)
  return out
}

const byRank = (a: ShiftTeamMember, b: ShiftTeamMember) => a.rank - b.rank || a.effectiveFrom.localeCompare(b.effectiveFrom)

function ErrorLine({ children }: { children: React.ReactNode }) {
  return (
    <p role="alert" className="mb-4 flex items-start gap-2 rounded-lg border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-[13px] text-destructive">
      <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
      {children}
    </p>
  )
}

/* ----------------------------------- Ekip oluştur ---------------------------------- */

function CreateTeamDialog({
  teams,
  onClose,
  onCreated,
}: {
  teams: ShiftTeam[]
  onClose: () => void
  onCreated: (id: string) => void
}) {
  const toast = useToast()
  const create = useCreateShiftTeam()
  const patterns = useShiftPatterns()
  const depts = useDepartments()

  const active = (patterns.data ?? []).filter((p) => p.isActive)
  const [name, setName] = useState('')
  const [patternId, setPatternId] = useState('')
  const [startDate, setStartDate] = useState(todayIso())
  const [startIndex, setStartIndex] = useState('0')
  const [departmentId, setDepartmentId] = useState(NO_DEPT)
  const [submitted, setSubmitted] = useState(false)

  const pattern = active.find((p) => p.id === patternId) ?? null
  const days = pattern ? sortedDays(pattern) : []
  const offset = Math.min(Number(startIndex) || 0, Math.max(0, days.length - 1))
  // Başlangıç tarihinde ekip desenin `offset`. günündeyse desen `offset` gün önce başlamış demektir.
  const anchorDate = addDays(startDate || todayIso(), -offset)

  const errors = {
    name: !name.trim()
      ? tx('Ekibe bir ad verin.')
      : teams.some((t) => t.name.toLocaleLowerCase(appLocale) === name.trim().toLocaleLowerCase(appLocale))
        ? tx('Bu adla bir ekip zaten var.')
        : undefined,
    pattern: !patternId ? 'Bir desen seçin.' : undefined,
    start: !startDate ? tx('Başlangıç tarihi gerekli.') : undefined,
  }
  const valid = !errors.name && !errors.pattern && !errors.start

  // Önizleme: başlangıçtan itibaren 14 gün; aynı deseni kullanan diğer ekipler de altta.
  const preview = dateRange(startDate || todayIso(), addDays(startDate || todayIso(), 13))
  const siblings = teams.filter((t) => t.shiftPatternId === patternId)

  const submit = () => {
    setSubmitted(true)
    if (!valid) return
    create.mutate(
      {
        name: name.trim(),
        shiftPatternId: patternId,
        anchorDate,
        departmentId: departmentId === NO_DEPT ? null : departmentId,
      },
      {
        onSuccess: (team) => {
          toast.ok(tx('"{0}" oluşturuldu. Şimdi üye ekleyebilirsiniz.', [name.trim()]))
          if (team?.id) onCreated(team.id)
          onClose()
        },
      },
    )
  }

  return (
    <Modal
      open
      onClose={onClose}
      size="lg"
      title={tx('Yeni vardiya ekibi')}
      note={tx('Ekip bir desene bağlanır ve seçtiğiniz tarihten itibaren o döngüyü takip eder.')}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            {tx('Vazgeç')}
          </Button>
          <Button onClick={submit} disabled={create.isPending || active.length === 0}>
            {create.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Ekibi oluştur')}
          </Button>
        </>
      }
    >
      {create.isError && <ErrorLine>{errorText(create.error, tx('Ekip oluşturulamadı.'))}</ErrorLine>}

      {!patterns.isPending && active.length === 0 ? (
        <EmptyState
          title={tx('Önce bir desen tanımlayın')}
          detail={tx('Ekip bir vardiya desenine bağlanmak zorunda. Desenler sekmesinden bir döngü oluşturun.')}
        />
      ) : (
        <div className="space-y-4">
          <TextField
            label={tx('Ekip adı')}
            required
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder={tx('ör. A Ekibi')}
            error={submitted ? errors.name : undefined}
            maxLength={80}
          />

          <SelectField
            label={tx('Desen')}
            required
            value={patternId}
            onChange={(v) => {
              setPatternId(v)
              setStartIndex('0')
            }}
            options={active.map((p) => ({ value: p.id, label: p.name }))}
            placeholder={patterns.isPending ? tx('Desenler yükleniyor') : tx('Desen seçin')}
            disabled={patterns.isPending}
            error={submitted ? errors.pattern : undefined}
            hint={pattern ? tx('{0} günlük döngü · {1}', [days.length, describeDays(days)]) : undefined}
          />

          <div className="grid gap-4 sm:grid-cols-2">
            <TextField
              label={tx('Başlangıç tarihi')}
              type="date"
              required
              value={startDate}
              onChange={(e) => setStartDate(e.target.value)}
              error={submitted ? errors.start : undefined}
            />
            <SelectField
              label={tx('Bu tarihte desenin hangi günü')}
              value={startIndex}
              onChange={setStartIndex}
              disabled={!pattern}
              placeholder={tx('Önce desen seçin')}
              options={days.map((d, i) => ({
                value: String(i),
                label: tx('{0}. gün · {1}{2}', [i + 1, DAY_STYLE[d.type].label, d.startTime ? ` ${timeRange(d.startTime, d.endTime)}` : '']),
              }))}
              hint={tx('7/24 kapsama için ekipleri aynı desenin farklı günlerinden başlatın.')}
            />
          </div>

          <SelectField
            label={tx('Departman')}
            value={departmentId}
            onChange={setDepartmentId}
            disabled={depts.isPending}
            options={[
              { value: NO_DEPT, label: tx('Kısıt yok — tüm çalışanlar eklenebilir') },
              ...depts.list.map((d) => ({ value: d.id, label: d.path })),
            ]}
            hint={tx('Seçerseniz üye eklerken yalnızca bu departmanın (ve alt departmanlarının) çalışanları listelenir.')}
          />

          {pattern && <LaborWarnings days={days} />}

          {pattern && (
            <div className="rounded-lg border border-border bg-muted/30 p-3">
              <p className="mb-2 text-[12px] font-medium text-muted-foreground">{tx('İlk iki hafta')}</p>
              <div className="overflow-x-auto">
                <table className="border-separate border-spacing-1 text-[11px]">
                  <thead>
                    <tr>
                      <th />
                      {preview.map((d) => (
                        <th key={d} className="w-7 text-center font-normal text-muted-foreground">
                          <span className="block">{weekdayShort(d)}</span>
                          <span className="tabular">{d.slice(8)}</span>
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    <tr>
                      <th className="pr-2 text-left font-semibold whitespace-nowrap">{name.trim() || tx('Yeni ekip')}</th>
                      {preview.map((d) => (
                        <td key={d}>
                          <DayBlock type={days[patternIndexAt(anchorDate, d, days.length)].type} size="sm" />
                        </td>
                      ))}
                    </tr>
                    {siblings.map((t) => (
                      <tr key={t.id} className="opacity-70">
                        <th className="pr-2 text-left font-normal whitespace-nowrap">{t.name}</th>
                        {preview.map((d) => (
                          <td key={d}>
                            <DayBlock type={days[patternIndexAt(t.anchorDate, d, days.length)].type} size="sm" />
                          </td>
                        ))}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              {siblings.length > 0 && (
                <p className="mt-2 text-[12px] text-muted-foreground">
                  {tx('Soluk satırlar bu deseni kullanan diğer ekipler — aynı gün aynı vardiyada çakışmadığını buradan görebilirsiniz.')}
                </p>
              )}
            </div>
          )}
        </div>
      )}
    </Modal>
  )
}

/* ------------------------------------ Üye ekle ------------------------------------ */

function AddMemberDialog({ team, teams, onClose }: { team: ShiftTeam; teams: ShiftTeam[]; onClose: () => void }) {
  const toast = useToast()
  const add = useAddShiftTeamMember()
  const people = usePeople()
  const depts = useDepartments()

  const members = team.members ?? []
  const nextRank = members.length ? Math.max(...members.map((m) => m.rank)) + 1 : 1

  const [employeeId, setEmployeeId] = useState<string | null>(null)
  const [rank, setRank] = useState(String(nextRank))
  const [tag, setTag] = useState('')
  const [effectiveFrom, setEffectiveFrom] = useState(todayIso())
  const [submitted, setSubmitted] = useState(false)

  const restricted = Boolean(team.departmentId)
  const allowedDepts = useMemo(
    () => (team.departmentId ? subtreeIds(depts.list, team.departmentId) : null),
    [depts.list, team.departmentId],
  )
  // Departman kısıtı için çalışanların güncel görevlendirmesi gerekir (yönetici ve üstü görür).
  const cannotFilter = restricted && !people.hasDetails

  const candidates = useMemo(() => {
    if (!allowedDepts) return people.list
    return people.list.filter((p) => {
      const d = people.departmentOf(p.id)
      return d !== null && allowedDepts.has(d)
    })
  }, [people, allowedDepts])

  /** Çalışanın şu an üye olduğu diğer vardiya ekibi. */
  const otherTeamOf = useMemo(() => {
    const map = new Map<string, string>()
    for (const t of teams) if (t.id !== team.id) for (const m of t.members ?? []) map.set(m.employeeId, t.name)
    return map
  }, [teams, team.id])

  const rankNum = Number(rank)
  const rankHolder = members.find((m) => m.rank === rankNum)
  const errors = {
    employee: !employeeId ? tx('Bir çalışan seçin.') : undefined,
    rank: !Number.isInteger(rankNum) || rankNum < 1 ? tx('Sıra 1 ya da daha büyük bir tam sayı olmalı.') : undefined,
    from: !effectiveFrom ? tx('Başlangıç tarihi gerekli.') : undefined,
  }
  const valid = !errors.employee && !errors.rank && !errors.from

  const tagOptions = useMemo(() => {
    const seen = new Set(TAG_SUGGESTIONS)
    for (const t of teams) for (const m of t.members ?? []) if (m.tag) seen.add(m.tag)
    return [...seen]
  }, [teams])

  const submit = () => {
    setSubmitted(true)
    if (!valid || !employeeId) return
    add.mutate(
      { teamId: team.id, input: { employeeId, rank: rankNum, tag: tag.trim() || null, effectiveFrom } },
      {
        onSuccess: () => {
          toast.ok(tx('{0} artık {1} üyesi.', [people.nameOf(employeeId), team.name]))
          onClose()
        },
      },
    )
  }

  const movingFrom = employeeId ? otherTeamOf.get(employeeId) : undefined

  return (
    <Modal
      open
      onClose={onClose}
      size="lg"
      title={tx('{0} · üye ekle', [team.name])}
      note={
        restricted
          ? tx('Bu ekip {0} departmanına bağlı; yalnızca o departmanın çalışanları listelenir.', [depts.pathOf(team.departmentId)])
          : tx('Ekibe departman kısıtı konmamış; tüm çalışanlar eklenebilir.')
      }
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            {tx('Vazgeç')}
          </Button>
          <Button onClick={submit} disabled={add.isPending || cannotFilter}>
            {add.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Ekibe ekle')}
          </Button>
        </>
      }
    >
      {add.isError && <ErrorLine>{errorText(add.error, tx('Üye eklenemedi.'))}</ErrorLine>}

      <div className="space-y-4">
        {cannotFilter ? (
          <ErrorLine>
            {tx('Departman kısıtını uygulamak için çalışanların departman bilgisi gerekiyor ama çalışan listesi alınamadı. Sayfayı yenileyip tekrar deneyin.')}
          </ErrorLine>
        ) : (
          <div>
            <p className="mb-1.5 text-[13px] font-medium">{tx('Çalışan')}</p>
            <PersonPicker
              people={candidates}
              value={employeeId}
              onChange={setEmployeeId}
              exclude={members.map((m) => m.employeeId)}
              height={220}
              note={(id) => (otherTeamOf.has(id) ? otherTeamOf.get(id)! : null)}
              detailOf={(id) => {
                const title = people.titleOf(id)
                const dept = people.departmentOf(id)
                return [title, dept ? depts.nameOf(dept) : null].filter(Boolean).join(' · ') || null
              }}
            />
            {candidates.length === 0 && restricted && !people.isPending && (
              <p className="mt-1.5 text-[12px] text-muted-foreground">{tx('Bu departmanda eklenebilecek çalışan yok.')}</p>
            )}
            {submitted && errors.employee && <p className="mt-1.5 text-[12px] text-destructive">{errors.employee}</p>}
            {movingFrom && (
              <p className="mt-2 rounded-lg border border-[hsl(var(--warning))]/30 bg-[hsl(var(--warning))]/8 px-3 py-2 text-[12px]">
                {tx('{0} şu an', [people.nameOf(employeeId)])}{' '}<strong>{movingFrom}</strong>{' '}{tx('üyesi. Eklerseniz oradaki üyeliği kendiliğinden sonlanır.')}
              </p>
            )}
          </div>
        )}

        <div className="grid gap-4 sm:grid-cols-3">
          <TextField
            label={tx('Sıra (rank)')}
            type="number"
            min={1}
            className="tabular"
            value={rank}
            onChange={(e) => setRank(e.target.value)}
            error={submitted ? errors.rank : undefined}
            hint={
              rankHolder
                ? tx('Bu sırada {0} var; ikisi art arda görünür.', [people.nameOf(rankHolder.employeeId)])
                : tx('1 en kıdemli; takvimde en üstte.')
            }
          />
          <div className="sm:col-span-2">
            <TextField
              label={tx('Etiket')}
              value={tag}
              onChange={(e) => setTag(e.target.value)}
              list="shift-tag-options"
              placeholder={tx('ör. Ekip Lideri')}
              hint={tx('İsteğe bağlı.')}
              maxLength={60}
            />
            <datalist id="shift-tag-options">
              {tagOptions.map((t) => (
                <option key={t} value={t} />
              ))}
            </datalist>
          </div>
        </div>

        <TextField
          label={tx('Ekipte başladığı tarih')}
          type="date"
          value={effectiveFrom}
          onChange={(e) => setEffectiveFrom(e.target.value)}
          error={submitted ? errors.from : undefined}
          className="sm:w-56"
        />
      </div>
    </Modal>
  )
}

/* ------------------------------------ Üye çıkar ----------------------------------- */

function RemoveMemberDialog({
  team,
  member,
  name,
  onClose,
}: {
  team: ShiftTeam
  member: ShiftTeamMember
  name: string
  onClose: () => void
}) {
  const toast = useToast()
  const remove = useRemoveShiftTeamMember()
  return (
    <Modal
      open
      onClose={onClose}
      title={tx('Üyelik sonlandırılsın mı?')}
      note={tx('{0}, {1} ekibinden bugün itibarıyla çıkarılır. Geçmiş üyelik kaydı silinmez.', [name, team.name])}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            {tx('Vazgeç')}
          </Button>
          <Button
            variant="destructive"
            disabled={remove.isPending}
            onClick={() =>
              remove.mutate(
                { teamId: team.id, memberId: member.id },
                {
                  onSuccess: () => {
                    toast.ok(tx('{0} ekipten çıkarıldı.', [name]))
                    onClose()
                  },
                },
              )
            }
          >
            {remove.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Ekipten çıkar')}
          </Button>
        </>
      }
    >
      {remove.isError ? (
        <ErrorLine>{errorText(remove.error, tx('Üyelik sonlandırılamadı.'))}</ErrorLine>
      ) : (
        <p className="text-[13px] text-muted-foreground">
          {tx('Çalışanın takvimi yarından itibaren bu ekibin desenini takip etmez. Başka bir ekibe eklemek için o ekibin sayfasından “Üye ekle”yi kullanın.')}
        </p>
      )}
    </Modal>
  )
}

/* ---------------------------------- Ekip düzenle ---------------------------------- */

/**
 * Ad, desen, döngü başlangıcı ve departman kısıtı düzeltilir. Takvim saklanmaz, hesaplanır:
 * desen ya da döngü başlangıcı değişirse geçmiş ve gelecek takvim yeni değerle yeniden çizilir.
 */
function EditTeamDialog({ team, teams, onClose }: { team: ShiftTeam; teams: ShiftTeam[]; onClose: () => void }) {
  const toast = useToast()
  const qc = useQueryClient()
  const patterns = useShiftPatterns()
  const depts = useDepartments()
  const [name, setName] = useState(team.name)
  const [patternId, setPatternId] = useState(team.shiftPatternId)
  const [anchorDate, setAnchorDate] = useState(team.anchorDate.slice(0, 10))
  const [departmentId, setDepartmentId] = useState(team.departmentId ?? NO_DEPT)
  const options = (patterns.data ?? []).filter((p) => p.isActive || p.id === team.shiftPatternId)
  const pattern = options.find((p) => p.id === patternId) ?? null
  const days = pattern ? sortedDays(pattern) : []
  const errors = {
    name: !name.trim()
      ? tx('Ekibe bir ad verin.')
      : teams.some((t) => t.id !== team.id && t.name.toLocaleLowerCase(appLocale) === name.trim().toLocaleLowerCase(appLocale))
        ? tx('Bu adla bir ekip zaten var.')
        : undefined,
    anchor: !anchorDate ? tx('Döngü başlangıcı gerekli.') : undefined,
  }
  const save = useMutation({
    mutationFn: () =>
      timeshiftApi.updateTeam(team.id, {
        name: name.trim(),
        anchorDate,
        departmentId: departmentId === NO_DEPT ? null : departmentId,
        departmentIdSet: true,
        ...(patternId !== team.shiftPatternId ? { shiftPatternId: patternId } : {}),
      }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: qke.root })
      toast.ok(tx('"{0}" güncellendi.', [name.trim()]))
      onClose()
    },
  })
  const changesCalendar = patternId !== team.shiftPatternId || anchorDate !== team.anchorDate.slice(0, 10)
  return (
    <Modal
      open
      onClose={onClose}
      size="lg"
      title={tx('{0} · düzenle', [team.name])}
      footer={
        <>
          <Button variant="outline" onClick={onClose}>
            {tx('Vazgeç')}
          </Button>
          <Button onClick={() => save.mutate()} disabled={save.isPending || Boolean(errors.name || errors.anchor)}>
            {save.isPending && <LoaderCircle className="size-4 animate-spin" />}
            {tx('Kaydet')}
          </Button>
        </>
      }
    >
      {save.isError && <ErrorLine>{errorText(save.error, tx('Ekip güncellenemedi.'))}</ErrorLine>}
      <div className="space-y-4">
        <TextField label={tx('Ekip adı')} required value={name} onChange={(e) => setName(e.target.value)} error={errors.name} maxLength={80} />
        <SelectField
          label={tx('Desen')}
          value={patternId}
          onChange={setPatternId}
          options={options.map((p) => ({ value: p.id, label: p.isActive ? p.name : tx('{0} (pasif)', [p.name]) }))}
          disabled={patterns.isPending}
          hint={pattern ? tx('{0} günlük döngü · {1}', [days.length, describeDays(days)]) : undefined}
        />
        <TextField
          label={tx('Döngü başlangıcı (desenin 1. günü)')}
          type="date"
          value={anchorDate}
          onChange={(e) => setAnchorDate(e.target.value)}
          error={errors.anchor}
          className="sm:w-56"
        />
        <SelectField
          label={tx('Departman')}
          value={departmentId}
          onChange={setDepartmentId}
          disabled={depts.isPending}
          options={[
            { value: NO_DEPT, label: tx('Kısıt yok — tüm çalışanlar eklenebilir') },
            ...depts.list.map((d) => ({ value: d.id, label: d.path })),
          ]}
        />
        {pattern && <LaborWarnings days={days} />}
        {changesCalendar && (
          <p className="rounded-lg border border-[hsl(var(--warning))]/30 bg-[hsl(var(--warning))]/8 px-3 py-2 text-[12px]">
            {tx('Desen ya da döngü başlangıcı değişirse ekibin takvimi (geçmiş günler dahil) yeni değerle yeniden hesaplanır.')}
          </p>
        )}
      </div>
    </Modal>
  )
}

/* ---------------------------------- Ekip ayrıntısı --------------------------------- */

function TeamDetail({
  team,
  teams,
  onOpenRoster,
}: {
  team: ShiftTeam
  teams: ShiftTeam[]
  onOpenRoster: (teamId: string) => void
}) {
  const { can } = useAuth()
  const manage = can('timeshift:manage')
  const people = usePeople()
  const depts = useDepartments()
  const [adding, setAdding] = useState(false)
  const [editing, setEditing] = useState(false)
  const [removing, setRemoving] = useState<ShiftTeamMember | null>(null)
  const confirm = useConfirm()
  const toast = useToast()
  const qc = useQueryClient()
  // Aktif üyesi olan ekip sunucuda 409 ile reddedilir; ileti aynen gösterilir.
  const del = useMutation({
    mutationFn: () => timeshiftApi.deleteTeam(team.id),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: qke.root })
      toast.ok(tx('"{0}" silindi.', [team.name]))
    },
    onError: (e) => toast.stop(errorText(e, tx('Ekip silinemedi.'))),
  })

  const days = team.shiftPattern ? sortedDays(team.shiftPattern) : []
  const today = todayIso()
  const notStarted = today < team.anchorDate.slice(0, 10)
  const todayIndex = days.length ? patternIndexAt(team.anchorDate, today, days.length) : 0
  const todayDay = days[todayIndex]
  const members = [...(team.members ?? [])].sort(byRank)

  return (
    <div className="space-y-4">
      <Panel>
        <PanelHead
          title={team.name}
          note={`${team.shiftPattern?.name ?? tx('Desen bulunamadı')} · ${team.departmentId ? depts.pathOf(team.departmentId) : tx('Departman kısıtı yok')}`}
          action={
            <span className="flex flex-wrap gap-1">
              <Button size="sm" variant="outline" onClick={() => onOpenRoster(team.id)}>
                <CalendarRange className="size-4" />{' '}{tx('Takvimi aç')}
              </Button>
              {manage && (
                <Button size="sm" variant="ghost" onClick={() => setEditing(true)}>
                  <Pencil className="size-4" />{' '}{tx('Düzenle')}
                </Button>
              )}
              {manage && (
                <Button
                  size="sm"
                  variant="ghost"
                  className="text-destructive hover:text-destructive"
                  disabled={del.isPending}
                  aria-label={tx('{0} ekibini sil', [team.name])}
                  onClick={async () => {
                    if (
                      await confirm({
                        title: tx('"{0}" ekibi silinsin mi?', [team.name]),
                        note: members.length
                          ? tx('Ekipte {0} aktif üye var; silmeden önce üyeleri çıkarın ya da başka ekibe taşıyın.', [members.length])
                          : tx('Ekip ve takvimi kalıcı olarak silinir.'),
                        action: tx('Sil'),
                      })
                    )
                      del.mutate()
                  }}
                >
                  <Trash2 className="size-4" />
                </Button>
              )}
            </span>
          }
        />
        <PanelBody className="space-y-3">
          {days.length > 0 ? (
            <>
              <PatternStrip days={days} size="sm" highlightIndex={notStarted ? undefined : todayIndex} />
              {notStarted ? (
                <p className="text-[13px] text-muted-foreground">
                  {tx('Döngü henüz başlamadı; başlangıç tarihi: {0}.', [formatDate(team.anchorDate)])}
                </p>
              ) : (
              <p className="text-[13px] text-muted-foreground">
                {tx('Bugün desenin')}{' '}<strong className="text-foreground">{tx('{0}. gününde', [todayIndex + 1])}</strong>
                {todayDay && (
                  <>
                    {' '}
                    — {DAY_STYLE[todayDay.type].lower}
                    {todayDay.startTime && ` ${timeRange(todayDay.startTime, todayDay.endTime)}`}
                  </>
                )}
                {tx('. Döngü başlangıcı:')}{' '}{formatDate(team.anchorDate)}.
              </p>
              )}
              <LaborWarnings days={days} />
            </>
          ) : (
            <p className="text-[13px] text-muted-foreground">{tx('Bu ekibin deseni yüklenemedi.')}</p>
          )}
        </PanelBody>
      </Panel>

      <Panel>
        <PanelHead
          title={tx('Üyeler')}
          note={members.length ? tx('{0} kişi · sıraya göre', [formatNumber(members.length)]) : undefined}
          action={
            manage && (
              <Button size="sm" onClick={() => setAdding(true)}>
                <UserPlus className="size-4" />{' '}{tx('Üye ekle')}
              </Button>
            )
          }
        />
        {members.length === 0 ? (
          <EmptyState
            icon={UsersRound}
            title={tx('Ekipte kimse yok')}
            detail={manage ? tx('Üye ekleyin; takvim onların adıyla dolar.') : tx('Üyeleri İK yöneticisi ekler.')}
            action={
              manage && (
                <Button size="sm" onClick={() => setAdding(true)}>
                  <UserPlus className="size-4" />{' '}{tx('Üye ekle')}
                </Button>
              )
            }
          />
        ) : (
          <ul className="divide-y divide-border">
            {members.map((m) => {
              const name = people.nameOf(m.employeeId)
              return (
                <li key={m.id} className="flex items-center gap-3 px-4 py-2.5">
                  <span
                    className="tabular flex size-7 shrink-0 items-center justify-center rounded-md bg-muted text-[12px] font-semibold"
                    title={tx('Sıra (rank)')}
                  >
                    {m.rank}
                  </span>
                  <PersonAvatar id={m.employeeId} name={name} />
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-[13px] font-medium">{name}</p>
                    <p className="truncate text-[12px] text-muted-foreground">
                      {[m.tag, people.titleOf(m.employeeId), tx('{0} itibarıyla', [formatDate(m.effectiveFrom)])]
                        .filter(Boolean)
                        .join(' · ')}
                    </p>
                  </div>
                  {manage && (
                    <Button
                      size="sm"
                      variant="ghost"
                      aria-label={tx('{0} ekipten çıkar', [name])}
                      onClick={() => setRemoving(m)}
                    >
                      <UserMinus className="size-4" />
                    </Button>
                  )}
                </li>
              )
            })}
          </ul>
        )}
      </Panel>

      {adding && <AddMemberDialog team={team} teams={teams} onClose={() => setAdding(false)} />}
      {editing && <EditTeamDialog team={team} teams={teams} onClose={() => setEditing(false)} />}
      {removing && (
        <RemoveMemberDialog
          team={team}
          member={removing}
          name={people.nameOf(removing.employeeId)}
          onClose={() => setRemoving(null)}
        />
      )}
    </div>
  )
}

/* -------------------------------------- Sayfa -------------------------------------- */

export function TeamsView({
  selectedId,
  onSelect,
  onOpenRoster,
}: {
  selectedId: string | null
  onSelect: (teamId: string) => void
  onOpenRoster: (teamId: string) => void
}) {
  const { can } = useAuth()
  const manage = can('timeshift:manage')
  const depts = useDepartments()
  const [deptFilter, setDeptFilter] = useState(ALL)
  const teams = useShiftTeams(deptFilter === ALL ? undefined : deptFilter)
  const allTeams = useShiftTeams()
  const [creating, setCreating] = useState(false)

  const list = useMemo(
    () => [...(teams.data ?? [])].sort((a, b) => a.name.localeCompare(b.name, 'tr-TR')),
    [teams.data],
  )
  const selected = list.find((t) => t.id === selectedId) ?? list[0] ?? null
  const today = todayIso()

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div className="w-full sm:w-72">
          <SelectField
            label={tx('Departman')}
            value={deptFilter}
            onChange={setDeptFilter}
            options={[{ value: ALL, label: tx('Tüm departmanlar') }, ...depts.list.map((d) => ({ value: d.id, label: d.path }))]}
          />
        </div>
        {manage && (
          <Button onClick={() => setCreating(true)}>
            <Plus className="size-4" />{' '}{tx('Yeni ekip')}
          </Button>
        )}
      </div>

      {teams.isPending ? (
        <Panel>
          <RowsSkeleton rows={4} columns={3} />
        </Panel>
      ) : teams.isError ? (
        <Panel>
          <ErrorState message={errorText(teams.error)} onRetry={() => void teams.refetch()} />
        </Panel>
      ) : list.length === 0 ? (
        <Panel>
          <EmptyState
            icon={UsersRound}
            title={deptFilter === ALL ? tx('Henüz vardiya ekibi yok') : tx('Bu departmanda vardiya ekibi yok')}
            detail={
              manage
                ? tx('Ekip, bir deseni belirli bir tarihten başlayarak takip eden çalışan grubudur.')
                : tx('Vardiya ekiplerini İK yöneticisi kurar.')
            }
            action={
              manage && (
                <Button onClick={() => setCreating(true)}>
                  <Plus className="size-4" />{' '}{tx('Ekip oluştur')}
                </Button>
              )
            }
          />
        </Panel>
      ) : (
        <div className="grid items-start gap-4 lg:grid-cols-[minmax(0,320px)_minmax(0,1fr)]">
          <Panel>
            <PanelHead title={tx('Ekipler')} note={tx('{0} ekip · simge bugünkü vardiya', [formatNumber(list.length)])} />
            <ul className="divide-y divide-border" aria-label={tx('Vardiya ekipleri')}>
              {list.map((t) => {
                const days = t.shiftPattern ? sortedDays(t.shiftPattern) : []
                const idx = days.length ? patternIndexAt(t.anchorDate, today, days.length) : 0
                const on = selected?.id === t.id
                return (
                  <li key={t.id}>
                    <button
                      type="button"
                      aria-current={on ? 'true' : undefined}
                      onClick={() => onSelect(t.id)}
                      className={cn(
                        'flex w-full cursor-pointer items-center gap-3 px-4 py-3 text-left transition-colors',
                        on ? 'bg-primary/8' : 'hover:bg-muted/50',
                      )}
                    >
                      {days[idx] ? (
                        <DayBlock type={days[idx].type} size="md" title={tx('Bugün: {0}', [DAY_STYLE[days[idx].type].label])} />
                      ) : (
                        <DayBlock type="Off" size="md" />
                      )}
                      <span className="min-w-0 flex-1">
                        <span className={cn('block truncate text-[13px]', on ? 'font-semibold' : 'font-medium')}>{t.name}</span>
                        <span className="block truncate text-[12px] text-muted-foreground">
                          {tx('{0} üye · {1}', [formatNumber(t.members?.length ?? 0), t.shiftPattern?.name ?? '—'])}
                        </span>
                      </span>
                    </button>
                  </li>
                )
              })}
            </ul>
          </Panel>

          {selected && <TeamDetail team={selected} teams={allTeams.data ?? list} onOpenRoster={onOpenRoster} />}
        </div>
      )}

      {creating && (
        <CreateTeamDialog teams={allTeams.data ?? list} onClose={() => setCreating(false)} onCreated={onSelect} />
      )}
    </div>
  )
}
