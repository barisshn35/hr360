/**
 * Organizasyon şeması — departman ağacı + içindeki çalışanlar, farklı yerleşimler,
 * renklendirme, matris bağları, odak/yol vurgusu, arama, küçük harita ve dışa aktarma.
 *
 * - Yerleşimler: dikey ağaç (HTML kutular; sürükle-bırak burada), yatay ağaç, radyal
 *   ağaç, halka (sunburst), ağaç haritası (treemap) ve liste/anahat. Yatay/radyal/
 *   halka/ağaç haritası SVG'dir ve tembel yüklenir (`OrgSvgView`); konum hesabı
 *   çizimden bağımsız `orgLayouts.ts`'te (ileride 3B görünüm aynı çıktıyı kullanacak).
 * - Görünüm durumu adreste (`orgViewState.ts`): bağlantıyı açan aynı görünümü görür.
 * - Hiyerarşi departman seviyesinde: kutular `parentDepartmentId` zincirine göre
 *   dizilir; çalışanlar yalnızca kendi departman kutusunun İÇİNDE durur.
 * - Sürükle-bırak native HTML5 olayları ile (projede DnD kütüphanesi yok).
 *   Bırakınca `POST /employees/{id}/assignments` çağrılır; iyimser güncelleme,
 *   hatada geri alma. Dokunmatik ekran ve klavye için her kartta "Taşı"
 *   düğmesi aynı işi bir diyalogla yapar.
 * - Büyük organizasyon: kalabalık şirkette departmanlar özet (kapalı) açılır,
 *   120'den fazla departmanda derin dallar kapalı başlar; kutu başına en fazla 8 kart.
 * - KVKK: renklendirmede ücret/yan hak yok; departman özetleri 5'ten az kişide
 *   gösterilmez (bkz. orgEncoding.ts); izin türü gösterilmez.
 */

import {
  createContext,
  lazy,
  Suspense,
  useCallback,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type DragEvent,
  type KeyboardEvent,
  type PointerEvent as ReactPointerEvent,
} from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import {
  ArrowRightLeft,
  Building2,
  ChevronDown,
  ChevronRight,
  ChevronUp,
  Crosshair,
  Crown,
  Download,
  GitFork,
  History,
  Link2,
  LoaderCircle,
  Maximize2,
  Minus,
  Plus,
  Presentation,
  TriangleAlert,
  UserRoundX,
  Waypoints,
  X,
} from 'lucide-react'
import { ApiError } from '@/api/client'
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query'
import { qk, useAddAssignment, useDepartmentList, useEmployees, useMyEmployeeId } from '@/api/queries'
import type { CreateAssignmentInput, Department, DepartmentLink } from '@/api/types'
import { useDirectory } from '@/api/directory'
import { leaveApi } from '@/api/leave'
import { governanceApi } from '@/api/governance'
import { engagementApi } from '@/api/engagement'
import { useTenantScope } from '@/lib/tenant-scope'
import { useAuth } from '@/auth/useAuth'
import { Panel } from '@/components/ui/Panel'
import { ErrorState, InfoNote, RowsSkeleton, EmptyState } from '@/components/ui/States'
import { Button } from '@/components/ui/button'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { useToast } from '@/components/ui/Toast'
import { formatDate, formatNumber } from '@/lib/format'
import { cn } from '@/lib/utils'
import { AvatarStack, PersonAvatar } from '@/features/performance/components/people'
import { errorText } from '@/features/performance/components/controls'
import { MoveEmployeeModal } from './MoveEmployeeModal'
import { activeAssignment, ancestorsOf, buildChart, type ChartDept, type ChartModel, type ChartPerson } from './orgChartModel'
import {
  applyViewParams,
  decodeViewState,
  defaultCollapsed,
  descendantCount,
  isInSubtree,
  navigate as navKey,
  PARAM,
  visibleRoots,
  type OrgEncoding,
  type OrgLayoutKind,
  type OrgViewState,
} from './orgViewState'
import {
  deptLeaveShare,
  deptTenureBand,
  LEAVE_BUCKETS,
  leaveColor,
  leaveLabel,
  NEUTRAL,
  nodeColor,
  ON_LEAVE_COLOR,
  smallGroupLegend,
  TENURE_BANDS,
  tenureBand,
  tenureColor,
  tenureLabel,
  deptColor,
  type LegendItem,
} from './orgEncoding'
import { OrgSearch, searchChart, type SearchHit } from './OrgSearch'
import { OrgOutline } from './OrgOutline'
import { OrgMinimap } from './OrgMinimap'
import { DepartmentLinksModal, useDepartmentLinks } from './DepartmentLinksModal'
import { tx, appLocale } from '@/lib/i18n'
import { ThreeDGate } from './ThreeDGate'
import { OrgTimeSlider } from './OrgTimeSlider'
import { DATE_PARAM, departmentsAt, monthStops, parseDateParam, snapshotEmployees, timeDelta } from './orgTimeline'
import {
  applyScenario,
  COMPARE_PARAM,
  DIFF_STATUSES,
  diffColor,
  diffLabel,
  diffSubtitle,
  parseCompareParam,
  parseScenarioParam,
  SCENARIO_PARAM,
  scenarioDiff,
} from './orgScenario'
import './orgchart.css'

import type { SvgLayoutKind } from './OrgSvgView'

const OrgSvgView = lazy(() => import('./OrgSvgView'))
// 3B: three.js yalnızca bu yerleşim seçilince indirilir (ayrı parça).
const Org3DView = lazy(() => import('./Org3DView'))

/** Kutu başına ilk açılışta görünen kart sayısı. */
const CARD_LIMIT = 8
/** Bu sayıyı aşan şirkette departmanlar kapalı (özet) açılır. */
const OPEN_ALL_UNDER = 60
/** Bu sayıdan çok departmanda küçük harita gösterilir. */
const MINIMAP_OVER = 15

const todayIso = () => {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

const norm = (s: string) => s.toLocaleLowerCase(appLocale)
const reducedMotion = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false

const layoutLabel = (k: OrgLayoutKind) =>
  ({
    vertical: tx('Dikey ağaç'),
    horizontal: tx('Yatay ağaç'),
    radial: tx('Radyal ağaç'),
    sunburst: tx('Halka (sunburst)'),
    treemap: tx('Ağaç haritası'),
    list: tx('Liste / anahat'),
    layers3d: tx('3B katmanlar'),
  })[k]

const encodingLabel = (e: OrgEncoding) =>
  ({ department: tx('Departmana göre'), tenure: tx('Kıdeme göre'), leave: tx('Bugün izinde') })[e]

/* ------------------------------------ Bağlam ------------------------------------ */

interface DragState {
  employeeId: string
  fromDeptId: string | null
}

interface ChartCtx {
  model: ChartModel
  /** Çalışan listesi okunabiliyor mu (yönetici ve üstü). Değilse kutular yalnızca departmanı gösterir. */
  peopleKnown: boolean
  canMove: boolean
  drag: DragState | null
  setDrag: (d: DragState | null) => void
  overId: string | null
  setOverId: (id: string | null) => void
  dropOn: (deptId: string) => void
  openMove: (employeeId: string) => void
  pending: Set<string>
  highlight: Set<string>
  nameOf: (id: string) => string
  isOpen: (deptId: string) => boolean
  toggleOpen: (deptId: string) => void
  showAll: Set<string>
  toggleShowAll: (deptId: string) => void
  collapsed: ReadonlySet<string>
  toggleKids: (deptId: string) => void
  colorOf: (deptId: string) => string
  /** Kişi kartındaki küçük işaret (kıdem bandı / izinde); yoksa null. */
  personMark: (p: ChartPerson) => { color: string; label: string } | null
  path: ReadonlySet<string>
  selectedDept: string | null
  selectedPerson: string | null
  deptMatches: ReadonlySet<string>
  select: (sel: string) => void
  focusOn: (deptId: string) => void
  onDeptKey: (e: KeyboardEvent<HTMLButtonElement>, deptId: string) => void
  /** Zaman kaydırıcısında kişi sayısı değişen departmanlar (kısa süre parlar). */
  pulse: ReadonlySet<string>
  pulseSeq: number
}

const Ctx = createContext<ChartCtx | null>(null)
const useChart = () => useContext(Ctx)!

/* ------------------------------------- Kart ------------------------------------- */

function PersonCard({ person, deptId, isHead }: { person: ChartPerson; deptId: string | null; isHead: boolean }) {
  const { canMove, drag, setDrag, setOverId, openMove, pending, highlight, personMark, selectedPerson, select } = useChart()
  const busy = pending.has(person.id)
  const lifted = drag?.employeeId === person.id
  const draggable = canMove && !busy
  const mark = personMark(person)
  const selected = selectedPerson === person.id

  const onDragStart = (e: DragEvent<HTMLDivElement>) => {
    e.dataTransfer.effectAllowed = 'move'
    e.dataTransfer.setData('text/plain', person.id)
    setDrag({ employeeId: person.id, fromDeptId: deptId })
  }

  return (
    <div
      id={`org-emp-${person.id}`}
      draggable={draggable}
      onDragStart={draggable ? onDragStart : undefined}
      onDragEnd={() => {
        setDrag(null)
        setOverId(null)
      }}
      onClick={(e) => {
        // Kartın boş yerine tıklamak kişiyi seçer (köke giden yol vurgulanır).
        if (!(e.target as HTMLElement).closest('a,button')) select(`p:${person.id}`)
      }}
      className={cn(
        'group/card relative flex items-center gap-2 rounded-lg border px-2 py-1.5 transition-[opacity,box-shadow] motion-reduce:transition-none',
        isHead
          ? 'border-[hsl(var(--warning))]/55 bg-[hsl(var(--warning))]/8'
          : 'border-border bg-background',
        draggable && 'cursor-grab active:cursor-grabbing',
        lifted && 'opacity-40',
        busy && 'opacity-70',
        highlight.has(person.id) && 'ring-2 ring-primary',
        selected && 'ring-2 ring-primary ring-offset-1 ring-offset-card',
      )}
    >
      <PersonAvatar id={person.id} name={person.name} size="sm" />
      <span className="min-w-0 flex-1">
        <span className="flex items-center gap-1">
          <Link
            to={`/panel/calisanlar/${person.id}`}
            draggable={false}
            className="truncate text-[12px] font-medium hover:underline"
          >
            {person.name}
          </Link>
          {isHead && <Crown aria-label={tx('Departman başı')} className="size-3.5 shrink-0 text-[hsl(var(--warning))]" />}
          {mark && (
            <span
              role="img"
              aria-label={mark.label}
              title={mark.label}
              className="size-2 shrink-0 rounded-full"
              style={{ background: mark.color }}
            />
          )}
        </span>
        <span className="block truncate text-[11px] text-muted-foreground">
          {isHead ? tx('Departman başı') : (person.title ?? tx('Unvan yok'))}
          {isHead && person.title ? ` · ${person.title}` : ''}
          {person.future && person.since ? tx(' · {0} itibarıyla', [formatDate(person.since)]) : ''}
        </span>
      </span>
      {busy ? (
        <LoaderCircle aria-label={tx('Kaydediliyor')} className="size-3.5 shrink-0 animate-spin text-muted-foreground" />
      ) : (
        canMove && (
          <button
            type="button"
            onClick={() => openMove(person.id)}
            aria-label={tx('{0} için departman değiştir', [person.name])}
            title={tx('Başka departmana taşı')}
            className="shrink-0 rounded p-1 text-muted-foreground opacity-0 transition-opacity group-hover/card:opacity-100 hover:bg-muted hover:text-foreground focus-visible:opacity-100 [@media(hover:none)]:opacity-100"
          >
            <ArrowRightLeft className="size-3.5" />
          </button>
        )
      )}
    </div>
  )
}

/* ------------------------------------- Kutu ------------------------------------- */

function DeptBox({ node }: { node: ChartDept }) {
  const c = useChart()
  const { dept, members, children } = node
  const open = c.isOpen(dept.id)
  const all = c.showAll.has(dept.id)
  const kidsHidden = c.collapsed.has(dept.id)
  const headId = dept.headEmployeeId ?? null
  const headIsMember = Boolean(headId && members.some((m) => m.id === headId))
  const onPath = c.path.has(dept.id)
  const selected = c.selectedDept === dept.id && !c.selectedPerson

  const droppable = Boolean(c.canMove && c.drag && c.drag.fromDeptId !== dept.id)
  const over = droppable && c.overId === dept.id
  const visible = all ? members : members.slice(0, CARD_LIMIT)
  const rest = members.length - visible.length

  const onDragOver = (e: DragEvent<HTMLDivElement>) => {
    if (!droppable) return
    e.preventDefault()
    e.dataTransfer.dropEffect = 'move'
    if (c.overId !== dept.id) c.setOverId(dept.id)
  }
  const onDragLeave = (e: DragEvent<HTMLDivElement>) => {
    if (!e.currentTarget.contains(e.relatedTarget as Node | null)) c.setOverId(null)
  }
  const onDrop = (e: DragEvent<HTMLDivElement>) => {
    e.preventDefault()
    c.setOverId(null)
    if (droppable) c.dropOn(dept.id)
  }

  const summary = c.peopleKnown
    ? tx('{0} kişi{1}', [formatNumber(members.length), children.length > 0 ? tx(' · {0} alt departman · toplam {1}', [children.length, formatNumber(node.total)]) : ''])
    : children.length > 0
      ? tx('{0} alt departman', [children.length])
      : tx('Departman')

  return (
    <div
      data-dept={dept.id}
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
      onDrop={onDrop}
      aria-dropeffect={droppable ? 'move' : undefined}
      className={cn(
        'relative w-64 overflow-hidden rounded-xl border bg-card text-left shadow-sm transition-[border-color,box-shadow,background-color] duration-150 motion-reduce:transition-none',
        over
          ? 'border-primary bg-primary/5 shadow-md ring-4 ring-primary/20'
          : droppable
            ? 'border-dashed border-primary/45'
            : selected
              ? 'border-primary ring-2 ring-primary/40'
              : onPath
                ? 'border-primary/60'
                : c.deptMatches.has(dept.id)
                  ? 'border-primary/50 ring-2 ring-primary/25'
                  : 'border-border',
      )}
    >
      <span aria-hidden className="absolute inset-y-0 left-0 w-1" style={{ background: c.colorOf(dept.id) }} />
      {c.pulse.has(dept.id) && <span key={c.pulseSeq} aria-hidden className="org-pulse-box absolute inset-0 rounded-xl" />}

      <div className="flex items-start gap-1 border-b border-border py-2 pr-1.5 pl-3.5">
        <button
          type="button"
          id={`org-dept-btn-${dept.id}`}
          onClick={() => c.select(`d:${dept.id}`)}
          onKeyDown={(e) => c.onDeptKey(e, dept.id)}
          aria-pressed={selected}
          aria-label={`${dept.name}, ${summary}`}
          aria-keyshortcuts="ArrowUp ArrowDown ArrowLeft ArrowRight F"
          className="min-w-0 flex-1 rounded-md text-left outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <span className={cn('block truncate text-[13px]', onPath ? 'font-bold' : 'font-semibold')} title={dept.name}>
            {dept.name}
          </span>
          <span className="tabular block text-[11px] text-muted-foreground">{summary}</span>
        </button>
        <Button
          size="icon-sm"
          variant="ghost"
          className="size-7"
          onClick={() => c.focusOn(dept.id)}
          aria-label={tx('{0} birimine odaklan', [dept.name])}
          title={tx('Bu birime odaklan')}
        >
          <Crosshair className="size-3.5" />
        </Button>
        {children.length > 0 && (
          <Button
            size="icon-sm"
            variant="ghost"
            className="size-7"
            onClick={() => c.toggleKids(dept.id)}
            aria-expanded={!kidsHidden}
            aria-label={kidsHidden ? tx('{0} alt departmanlarını göster', [dept.name]) : tx('{0} alt departmanlarını gizle', [dept.name])}
            title={kidsHidden ? tx('Alt departmanları göster') : tx('Alt departmanları gizle')}
          >
            <GitFork className={cn('size-3.5', kidsHidden && 'text-primary')} />
          </Button>
        )}
        {c.peopleKnown && members.length > 0 && (
          <Button
            size="icon-sm"
            variant="ghost"
            className="size-7"
            onClick={() => c.toggleOpen(dept.id)}
            aria-expanded={open}
            aria-label={open ? tx('{0} çalışanlarını gizle', [dept.name]) : tx('{0} çalışanlarını göster', [dept.name])}
          >
            {open ? <ChevronUp className="size-4" /> : <ChevronDown className="size-4" />}
          </Button>
        )}
      </div>

      {headId && !headIsMember && (
        <p className="flex items-center gap-1.5 border-b border-border py-1.5 pr-2 pl-3.5 text-[11px] text-muted-foreground">
          <Crown className="size-3.5 shrink-0 text-[hsl(var(--warning))]" aria-hidden />
          <span className="truncate">
            {tx('Başı:')}{' '}<span className="font-medium text-foreground">{c.nameOf(headId)}</span>
            {c.peopleKnown && tx(' (başka departmanda kayıtlı)')}
          </span>
        </p>
      )}

      {!c.peopleKnown ? null : members.length === 0 ? (
        <p className="py-2.5 pr-2 pl-3.5 text-[12px] text-muted-foreground">
          {over ? tx('Bırakın, buraya atansın.') : tx('Bu departmanda çalışan yok.')}
        </p>
      ) : open ? (
        <div className="space-y-1 p-2 pl-3">
          {visible.map((p) => (
            <PersonCard key={p.id} person={p} deptId={dept.id} isHead={p.id === headId} />
          ))}
          {(rest > 0 || all) && members.length > CARD_LIMIT && (
            <button
              type="button"
              onClick={() => c.toggleShowAll(dept.id)}
              className="w-full rounded-md py-1 text-[12px] font-medium text-primary hover:bg-primary/5"
            >
              {all ? tx('Daha az göster') : tx('+{0} kişi daha', [formatNumber(rest)])}
            </button>
          )}
        </div>
      ) : (
        <button
          type="button"
          onClick={() => c.toggleOpen(dept.id)}
          className="flex w-full items-center gap-2 py-2 pr-2 pl-3.5 text-left hover:bg-muted/50"
        >
          <AvatarStack people={members} max={5} />
          <span className="text-[12px] text-muted-foreground">{tx('Çalışanları göster')}</span>
        </button>
      )}

      {kidsHidden && (
        <button
          type="button"
          onClick={() => c.toggleKids(dept.id)}
          className="w-full border-t border-border py-1 text-[11.5px] font-medium text-primary hover:bg-primary/5"
        >
          {tx('+{0} alt birim gizli', [descendantCount(node)])}
        </button>
      )}

      {over && (
        <p className="border-t border-primary/30 bg-primary/10 py-1.5 pr-2 pl-3.5 text-[11px] font-medium text-primary">{tx('Bırakınca {0} bu departmana atanır', [c.nameOf(c.drag!.employeeId)])}</p>
      )}
    </div>
  )
}

function Branch({ node }: { node: ChartDept }) {
  const { collapsed, path } = useChart()
  return (
    <li className={path.has(node.dept.id) ? 'on-path' : undefined}>
      <DeptBox node={node} />
      {node.children.length > 0 && !collapsed.has(node.dept.id) && (
        <ul>
          {node.children.map((c) => (
            <Branch key={c.dept.id} node={c} />
          ))}
        </ul>
      )}
    </li>
  )
}

/* ------------------------------ Matris katmanı (dikey) ------------------------------ */

interface Rect {
  x: number
  y: number
  w: number
  h: number
}

/** Dikey (HTML) ağaçta kutu konumlarını ölçer — matris eğrileri ve küçük harita için. */
function useMeasuredBoxes(viewport: React.RefObject<HTMLDivElement | null>, canvas: React.RefObject<HTMLDivElement | null>, enabled: boolean, deps: unknown[]) {
  const [rects, setRects] = useState<Map<string, Rect>>(new Map())
  const [size, setSize] = useState({ w: 0, h: 0 })
  const measure = useCallback(() => {
    const vp = viewport.current
    const cv = canvas.current
    if (!vp || !cv) return
    const base = vp.getBoundingClientRect()
    const next = new Map<string, Rect>()
    cv.querySelectorAll<HTMLElement>('[data-dept]').forEach((el) => {
      const r = el.getBoundingClientRect()
      next.set(el.dataset.dept!, { x: r.left - base.left + vp.scrollLeft, y: r.top - base.top + vp.scrollTop, w: r.width, h: r.height })
    })
    setRects(next)
    setSize({ w: vp.scrollWidth, h: vp.scrollHeight })
  }, [viewport, canvas])

  useLayoutEffect(() => {
    if (!enabled) return
    measure()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabled, measure, ...deps])

  useEffect(() => {
    const cv = canvas.current
    if (!enabled || !cv) return
    let raf = 0
    const ro = new ResizeObserver(() => {
      cancelAnimationFrame(raf)
      raf = requestAnimationFrame(measure)
    })
    ro.observe(cv)
    return () => {
      cancelAnimationFrame(raf)
      ro.disconnect()
    }
  }, [enabled, measure, canvas])

  return { rects, size }
}

function matrixCurve(a: Rect, b: Rect): string {
  const acx = a.x + a.w / 2
  const acy = a.y + a.h / 2
  const bcx = b.x + b.w / 2
  const bcy = b.y + b.h / 2
  // Kutunun hedefe bakan kenarından çık.
  const side = (r: Rect, cx: number, cy: number, tx: number, ty: number) =>
    Math.abs(tx - cx) * r.h > Math.abs(ty - cy) * r.w
      ? { x: cx + Math.sign(tx - cx) * (r.w / 2), y: cy }
      : { x: cx, y: cy + Math.sign(ty - cy) * (r.h / 2) }
  const p = side(a, acx, acy, bcx, bcy)
  const q = side(b, bcx, bcy, acx, acy)
  const dx = q.x - p.x
  const dy = q.y - p.y
  const len = Math.hypot(dx, dy) || 1
  const bend = Math.min(140, len * 0.25)
  const cx = (p.x + q.x) / 2 + (-dy / len) * bend
  const cy = (p.y + q.y) / 2 + (dx / len) * bend
  return `M${p.x},${p.y}Q${cx},${cy} ${q.x},${q.y}`
}

/* ------------------------------------- Şema ------------------------------------- */

export function OrgChart({ companyId, companyName }: { companyId: string; companyName: string }) {
  const { can, hasRole } = useAuth()
  const toast = useToast()
  const routerNavigate = useNavigate()
  const canSeePeople = can('employee:viewAll')
  // Backend ucu RequireHrAdmin; bu projede employee:manage yalnızca hr-admin ve üstünde.
  const canMove = can('employee:manage')
  const canManageLinks = can('organization:manage')
  const isHr = hasRole('hr-admin') || hasRole('tenant-admin') || hasRole('platform-admin')

  const departments = useDepartmentList(companyId)
  const employees = useEmployees({ enabled: canSeePeople })
  const directory = useDirectory()
  const move = useAddAssignment()
  const qc = useQueryClient()

  const today = todayIso()
  const { hasTenant } = useTenantScope()
  const [searchParams, setSearchParams] = useSearchParams()
  /** Görünüm dışı adres parametreleri (tarih, senaryo); diğerleri korunur. */
  const setExtra = useCallback(
    (patch: Record<string, string | null>) =>
      setSearchParams(
        (prev) => {
          const next = new URLSearchParams(prev)
          for (const [k, v] of Object.entries(patch)) {
            if (v === null) next.delete(k)
            else next.set(k, v)
          }
          return next
        },
        { replace: true },
      ),
    [setSearchParams],
  )

  /* ------------------------------- zaman kaydırıcısı ------------------------------- */
  // Zaman makinesi verisi kişi adı içerir: yönetici ve üstü + plan özelliği (zaman makinesi ekranıyla aynı).
  const canTime = canSeePeople && can('performance:manage') && hasTenant
  const date = canTime ? parseDateParam(searchParams.get(DATE_PARAM), today) : null
  const [timeOpen, setTimeOpen] = useState(false)
  const stops = useMemo(() => monthStops(today, 24), [today])
  const snap = useQuery({
    queryKey: ['time-machine', date],
    queryFn: ({ signal }) => governanceApi.snapshot(date!, signal),
    enabled: Boolean(date),
    placeholderData: keepPreviousData,
    staleTime: 5 * 60_000,
  })

  const liveModel = useMemo(
    () => buildChart(departments.data ?? [], employees.data ?? [], today),
    [departments.data, employees.data, today],
  )
  const timeModel = useMemo(
    () =>
      date && snap.data
        ? buildChart(departmentsAt(departments.data ?? [], snap.data), snapshotEmployees(snap.data), String(snap.data.date).slice(0, 10))
        : null,
    [date, snap.data, departments.data],
  )
  const model = timeModel ?? liveModel
  /** Renklendirmenin "bugün"ü: geçmiş tarihte kıdem o güne göre. */
  const refDay = date ?? today

  // Kaydırıcı ilerleyince kişi sayısı değişen/yeni departmanlar kısa süre parlar.
  const [pulse, setPulse] = useState<{ ids: Set<string>; seq: number }>({ ids: new Set(), seq: 0 })
  const prevModel = useRef<{ model: typeof model; date: string | null } | null>(null)
  useEffect(() => {
    const prev = prevModel.current
    if (prev?.model === model) return
    prevModel.current = { model, date }
    // Yalnızca zaman kaydırıcısı etkinken (canlı verinin yenilenmesi parlatmaz).
    if (!prev || (prev.date === null && date === null)) return
    const d = timeDelta(prev.model, model)
    setPulse((p) => ({ ids: new Set([...d.changed.keys(), ...d.added]), seq: p.seq + 1 }))
  }, [model, date])

  /* ------------------------------- senaryo karşılaştırma ------------------------------- */
  const canScenario = canSeePeople && can('performance:manage') && hasTenant
  const scenarios = useQuery({
    queryKey: ['org-scenarios'],
    queryFn: ({ signal }) => engagementApi.orgScenarios(signal),
    enabled: canScenario,
    staleTime: 60_000,
  })
  const scenarioId = canScenario && !date ? parseScenarioParam(searchParams.get(SCENARIO_PARAM)) : null
  const scenario = scenarioId ? scenarios.data?.find((x) => x.id === scenarioId) : undefined
  const compare = parseCompareParam(searchParams.get(COMPARE_PARAM))
  const draftModel = useMemo(
    () => (scenario ? buildChart(departments.data ?? [], applyScenario(employees.data ?? [], scenario.moves, today), today) : null),
    [scenario, departments.data, employees.data, today],
  )
  const diff = useMemo(() => (draftModel ? scenarioDiff(liveModel, draftModel) : null), [draftModel, liveModel])

  const nameOf = useMemo(() => {
    const names = new Map<string, string>()
    for (const d of directory.data ?? []) names.set(d.id, d.fullName || `${d.firstName} ${d.lastName}`.trim())
    for (const e of employees.data ?? []) names.set(e.id, `${e.firstName} ${e.lastName}`.trim())
    return (id: string) => names.get(id) ?? tx('Bilinmeyen çalışan')
  }, [directory.data, employees.data])

  const pathOf = useCallback(
    (deptId: string) =>
      ancestorsOf(model, deptId)
        .map((id) => model.byId.get(id)?.dept.name ?? '')
        .join(' › '),
    [model],
  )

  /* ------------------------------- adres durumu ------------------------------- */
  const deptIds = useMemo(() => [...model.byId.keys()], [model])
  const url = useMemo(() => decodeViewState(searchParams, deptIds), [searchParams, deptIds])
  // Geçmiş tarihte "bugün izinde" anlamsız; senaryo karşılaştırmasında renk farkı gösterir.
  const encoding: OrgEncoding = !canSeePeople || (date && url.encoding === 'leave') ? 'department' : url.encoding
  /** Geçmiş tarihte şema salt okunur (taşıma bugüne yazılırdı). */
  const canMoveNow = canMove && !date && !diff
  const layout = url.layout
  const focus = url.focus
  const collapsed = useMemo(() => url.collapsed ?? defaultCollapsed(model), [url.collapsed, model])

  const update = useCallback(
    (patch: Partial<OrgViewState>) =>
      setSearchParams(
        (prev) => {
          const cur = decodeViewState(prev, deptIds)
          return applyViewParams(prev, { ...cur, ...patch })
        },
        { replace: true },
      ),
    [setSearchParams, deptIds],
  )

  const toggleKids = (id: string) => {
    const n = new Set(collapsed)
    if (n.has(id)) n.delete(id)
    else n.add(id)
    update({ collapsed: n })
  }

  /* ------------------------------- seçim ve yol ------------------------------- */
  const selectedPerson = url.selected?.startsWith('p:') ? url.selected.slice(2) : null
  const selectedDept = url.selected?.startsWith('d:')
    ? url.selected.slice(2)
    : selectedPerson
      ? (model.deptOf.get(selectedPerson) ?? null)
      : null
  const path = useMemo(() => {
    const s = new Set<string>(selectedDept ? ancestorsOf(model, selectedDept) : [])
    if (s.size) s.add('__company__')
    return s
  }, [model, selectedDept])

  const select = (sel: string) => update({ selected: url.selected === sel ? null : sel })
  const focusOn = (deptId: string | null) => update({ focus: deptId, selected: deptId ? `d:${deptId}` : url.selected })

  /* ------------------------------- görünüm durumu ------------------------------- */
  const personCount = model.deptOf.size
  const [openSet, setOpenSet] = useState<Set<string> | null>(null)
  const defaultOpen = personCount <= OPEN_ALL_UNDER
  const isOpen = (id: string) => (openSet ? openSet.has(id) : defaultOpen)
  const openBox = (id: string) =>
    setOpenSet((prev) => {
      if (prev === null && defaultOpen) return prev
      const n = new Set(prev ?? (defaultOpen ? model.byId.keys() : []))
      n.add(id)
      return n
    })
  const toggleOpen = (id: string) =>
    setOpenSet((prev) => {
      const base = prev ?? new Set(defaultOpen ? model.byId.keys() : [])
      const next = new Set(base)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  const [showAll, setShowAll] = useState<Set<string>>(new Set())
  const toggleShowAll = (id: string) =>
    setShowAll((s) => {
      const n = new Set(s)
      if (n.has(id)) n.delete(id)
      else n.add(id)
      return n
    })

  const [drag, setDrag] = useState<DragState | null>(null)
  const [overId, setOverId] = useState<string | null>(null)
  const [pending, setPending] = useState<Set<string>>(new Set())
  const [moving, setMoving] = useState<string | null>(null)
  const [linksOpen, setLinksOpen] = useState(false)

  /* ------------------------------- renklendirme ------------------------------- */
  const me = useMyEmployeeId(encoding === 'leave' && !isHr)
  const leave = useQuery({
    queryKey: ['leave', 'approved-on', today],
    queryFn: ({ signal }) => leaveApi.approvedOnDay(today, signal),
    enabled: canSeePeople && encoding === 'leave',
    staleTime: 5 * 60_000,
  })
  const onLeave = useMemo(
    () =>
      leave.data
        ? new Set(leave.data.filter((r) => r.startDate.slice(0, 10) <= today && r.endDate.slice(0, 10) >= today).map((r) => r.employeeId))
        : null,
    [leave.data, today],
  )
  /**
   * İzin kaydını görebildiğimiz departmanlar: İK hepsini; yönetici yalnızca başı olduğu
   * departmanları (sunucu kapsamıyla aynı). Diğerleri "bilinmiyor" (nötr) kalır, yanlışlıkla
   * "izinde kimse yok" görünmez.
   */
  const leaveScope = useMemo(() => {
    if (isHr) return null
    const s = new Set<string>()
    if (me.employeeId) for (const n of model.byId.values()) if (n.dept.headEmployeeId === me.employeeId) s.add(n.dept.id)
    return s
  }, [isHr, me.employeeId, model])

  const colorOf = useCallback(
    (deptId: string) => {
      if (diff) return diffColor(diff.byDept.get(deptId)?.status ?? 'same')
      const node = model.byId.get(deptId)
      if (!node) return NEUTRAL
      if (encoding === 'leave' && leaveScope && !leaveScope.has(deptId)) return NEUTRAL
      return nodeColor(encoding, node, node.colorIndex, { today: refDay, onLeave })
    },
    [model, encoding, refDay, onLeave, leaveScope, diff],
  )

  const personMark = (p: ChartPerson): { color: string; label: string } | null => {
    if (encoding === 'tenure') {
      const b = tenureBand(p.hireDate, refDay)
      return b ? { color: tenureColor(b), label: tx('Kıdem: {0}', [tenureLabel(b)]) } : null
    }
    if (encoding === 'leave' && onLeave?.has(p.id)) return { color: ON_LEAVE_COLOR, label: tx('Bugün izinde') }
    return null
  }

  const subtitleOf = useCallback(
    (deptId: string) => {
      if (diff) return diffSubtitle(diff.byDept.get(deptId))
      const n = model.byId.get(deptId)
      if (!n) return ''
      if (!canSeePeople) return n.children.length ? tx('{0} alt departman', [n.children.length]) : tx('Departman')
      let s = tx('{0} kişi', [formatNumber(n.total)])
      if (encoding === 'tenure') {
        const b = deptTenureBand(n, refDay)
        if (b) s += tx(' · ortanca {0}', [tenureLabel(b)])
      } else if (encoding === 'leave' && onLeave && (!leaveScope || leaveScope.has(deptId))) {
        const share = deptLeaveShare(n, onLeave)
        if (share !== null) s += tx(' · %{0} izinde', [Math.round(share * 100)])
      }
      return s
    },
    [model, canSeePeople, encoding, refDay, onLeave, leaveScope, diff],
  )

  /* Yan yana karşılaştırmada "Bugün" paneli: fark renkleri/alt yazıları yalnızca senaryo tarafında. */
  const liveColorOf = useCallback(
    (deptId: string) => {
      const node = liveModel.byId.get(deptId)
      if (!node) return NEUTRAL
      if (encoding === 'leave' && leaveScope && !leaveScope.has(deptId)) return NEUTRAL
      return nodeColor(encoding, node, node.colorIndex, { today: refDay, onLeave })
    },
    [liveModel, encoding, refDay, onLeave, leaveScope],
  )
  const liveSubtitleOf = useCallback(
    (deptId: string) => {
      const n = liveModel.byId.get(deptId)
      if (!n) return ''
      if (!canSeePeople) return n.children.length ? tx('{0} alt departman', [n.children.length]) : tx('Departman')
      return tx('{0} kişi', [formatNumber(n.total)])
    },
    [liveModel, canSeePeople],
  )

  const legend: LegendItem[] = useMemo(() => {
    if (diff) return DIFF_STATUSES.map((st) => ({ key: st, label: diffLabel(st), color: diffColor(st) }))
    if (encoding === 'tenure') return [...TENURE_BANDS.map((b) => ({ key: b, label: tenureLabel(b), color: tenureColor(b) })), smallGroupLegend()]
    if (encoding === 'leave') return [...LEAVE_BUCKETS.map((b) => ({ key: b, label: leaveLabel(b), color: leaveColor(b) })), smallGroupLegend()]
    return model.roots.slice(0, 8).map((r) => ({ key: r.dept.id, label: r.dept.name, color: deptColor(r.colorIndex) }))
  }, [encoding, model, diff])

  /* ------------------------------------ matris ------------------------------------ */
  const links = useDepartmentLinks(companyId, url.matrix)
  const matrix: DepartmentLink[] = useMemo(
    () => (links.data ?? []).filter((l) => model.byId.has(l.fromDepartmentId) && model.byId.has(l.toDepartmentId)),
    [links.data, model],
  )
  const linkCount = (deptId: string) => matrix.filter((l) => l.fromDepartmentId === deptId || l.toDepartmentId === deptId).length

  /* ------------------------------------ taşıma ------------------------------------ */
  const currentActive = (employeeId: string) => {
    const e = employees.data?.find((x) => x.id === employeeId)
    return e ? activeAssignment(e) : null
  }
  const currentTitle = (employeeId: string) => currentActive(employeeId)?.positionTitle ?? null

  const performMove = (employeeId: string, input: CreateAssignmentInput) => {
    const fromId = model.deptOf.get(employeeId) ?? null
    if (fromId === input.departmentId) return
    const name = nameOf(employeeId)
    const toName = model.byId.get(input.departmentId)?.dept.name ?? 'yeni'
    const wasHeadOf = fromId && model.byId.get(fromId)?.dept.headEmployeeId === employeeId ? model.byId.get(fromId)!.dept : null

    setPending((s) => new Set(s).add(employeeId))
    // Hedef kutu kapalıysa açılsın: kişi taşındığı yerde hemen görünsün.
    if (!isOpen(input.departmentId)) toggleOpen(input.departmentId)
    move.mutate(
      { employeeId, input },
      {
        onSuccess: () => {
          toast.ok(tx('{0} artık {1} departmanında.', [name, toName]))
          // Backend eski departmanın başını kendisi temizler (best-effort). Departmanlar bu noktada
          // yeniden çekilmiş olur; uyarı yalnızca temizleme gerçekleşmediyse çıkar.
          if (wasHeadOf) {
            const fresh = qc.getQueryData<Department[]>(qk.departments(companyId))
            if (fresh?.find((d) => d.id === wasHeadOf.id)?.headEmployeeId === employeeId) {
              toast.info(tx('{0} hâlâ {1} departmanının başı olarak kayıtlı. Departman başını ayrıca güncelleyin.', [name, wasHeadOf.name]))
            }
          }
        },
        onError: (error) => {
          if (error instanceof ApiError && error.status === 403) {
            toast.stop(tx('Departman değiştirme yetkiniz yok. Bu işlemi yalnızca İK yöneticisi yapabilir; değişiklik geri alındı.'))
          } else {
            toast.stop(tx('{0} taşınamadı, eski departmanına geri alındı. {1}', [name, errorText(error)]))
          }
        },
        onSettled: () =>
          setPending((s) => {
            const n = new Set(s)
            n.delete(employeeId)
            return n
          }),
      },
    )
  }

  const dropOn = (deptId: string) => {
    if (!drag) return
    const { employeeId } = drag
    setDrag(null)
    // Bugünden itibaren; kişinin ileri tarihli bir ataması varsa o tarih (aynı gün → backend günceller,
    // geriye dönük tarih 400 alırdı).
    const activeFrom = currentActive(employeeId)?.effectiveFrom.slice(0, 10)
    const effectiveFrom = activeFrom && activeFrom > today ? activeFrom : today
    performMove(employeeId, { departmentId: deptId, positionTitle: currentTitle(employeeId) ?? undefined, effectiveFrom })
  }

  /* ------------------------------------ arama ------------------------------------ */
  const [query, setQuery] = useState('')
  const q = norm(query.trim())
  const highlight = useMemo(() => {
    if (q.length < 2) return new Set<string>()
    const hits = new Set<string>()
    for (const node of model.byId.values()) for (const p of node.members) if (norm(p.name).includes(q)) hits.add(p.id)
    for (const p of model.unassigned) if (norm(p.name).includes(q)) hits.add(p.id)
    return hits
  }, [q, model])
  const deptMatches = useMemo(() => {
    const s = new Set<string>()
    if (q.length < 2) return s
    for (const n of model.byId.values()) if (norm(n.dept.name).includes(q)) s.add(n.dept.id)
    // SVG görünümlerinde kişi düğümü yok: eşleşen kişinin departmanı işaretlenir.
    if (layout !== 'vertical') for (const pid of highlight) { const d = model.deptOf.get(pid); if (d) s.add(d) }
    return s
  }, [q, model, highlight, layout])
  const hits = useMemo(() => searchChart(model, query, canSeePeople, pathOf), [model, query, canSeePeople, pathOf])

  const [jump, setJump] = useState<{ kind: 'd' | 'p'; id: string; seq: number } | null>(null)
  const [centerRequest, setCenterRequest] = useState<{ id: string; seq: number } | null>(null)

  const goTo = (hit: Pick<SearchHit, 'kind' | 'id'>) => {
    const deptId = hit.kind === 'd' ? hit.id : model.deptOf.get(hit.id)
    if (!deptId) {
      // Atanmamış kişi: şemada yeri yok, aşağıdaki listede.
      update({ selected: `p:${hit.id}` })
      setJump({ kind: 'p', id: hit.id, seq: Date.now() })
      return
    }
    const chain = ancestorsOf(model, deptId)
    const nextCollapsed = new Set(collapsed)
    // Hedefin ataları açılsın (departmanın kendisi değil: kişi zaten kutusunda).
    chain.slice(0, -1).forEach((id) => nextCollapsed.delete(id))
    const nextFocus = focus && !isInSubtree(model, focus, deptId) ? null : focus
    update({ selected: `${hit.kind}:${hit.id}`, collapsed: nextCollapsed, focus: nextFocus })
    if (hit.kind === 'p') {
      openBox(deptId)
      const node = model.byId.get(deptId)
      if (node && node.members.findIndex((m) => m.id === hit.id) >= CARD_LIMIT) setShowAll((s) => new Set(s).add(deptId))
    }
    setJump({ kind: hit.kind, id: hit.id, seq: Date.now() })
    setCenterRequest({ id: deptId, seq: Date.now() })
  }

  // Dikey/liste görünümünde hedefi ekrana kaydır.
  useEffect(() => {
    if (!jump || (layout !== 'vertical' && jump.kind === 'd')) return
    const t = window.setTimeout(() => {
      const el =
        jump.kind === 'p' ? document.getElementById(`org-emp-${jump.id}`) : document.getElementById(`org-dept-btn-${jump.id}`)
      el?.scrollIntoView({ behavior: reducedMotion() ? 'auto' : 'smooth', block: 'center', inline: 'center' })
    }, 80)
    return () => window.clearTimeout(t)
  }, [jump, layout])

  /* ------------------------------ yakınlaştır / kaydır ------------------------------ */
  // Yakınlık sık değişir (tekerlek); yerel tutulur, adrese gecikmeli yazılır.
  const initial = useRef({ zoomFromUrl: searchParams.has(PARAM.zoom), layout })
  const [zoom, setZoom] = useState(url.zoom)
  const [zoomRight, setZoomRight] = useState(1)
  const [fitRequest, setFitRequest] = useState(0)
  const viewport = useRef<HTMLDivElement>(null)
  const canvas = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (Math.abs(zoom - url.zoom) < 0.005) return
    const t = window.setTimeout(() => update({ zoom }), 400)
    return () => window.clearTimeout(t)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [zoom])

  const fitVertical = () => {
    const vp = viewport.current
    const cv = canvas.current
    if (!vp || !cv) return
    const natural = cv.scrollWidth
    setZoom(Math.max(0.4, Math.min(1, +((vp.clientWidth - 32) / natural).toFixed(2))))
  }
  const fit = () => (layout === 'vertical' ? fitVertical() : layout === 'list' ? undefined : setFitRequest((n) => n + 1))

  // İlk açılışta (adreste yakınlık yoksa) dikey şema ekrandan genişse bir kez sığdır; en fazla
  // %60'a kadar küçült ki kartlar okunur kalsın. Başka yerleşimden dikeye dönünce de sığdırılır.
  const fittedFor = useRef<string | null>(null)
  const firstFit = useRef(true)
  // Tuval ancak tüm veri gelince çizilir; ölçüm ondan önce yapılırsa boşa gider.
  const ready = model.roots.length > 0 && !departments.isPending && !(canSeePeople && employees.isPending)
  useEffect(() => {
    if (!ready || layout !== 'vertical') {
      if (layout !== 'vertical') fittedFor.current = null
      return
    }
    const key = `vertical|${focus ?? ''}`
    if (fittedFor.current === key) return
    const skip = firstFit.current && initial.current.zoomFromUrl && initial.current.layout === 'vertical'
    firstFit.current = false
    fittedFor.current = key
    if (skip) return
    const t = window.setTimeout(() => {
      const vp = viewport.current
      const cv = canvas.current
      if (!vp || !cv) return
      const ratio = (vp.clientWidth - 32) / cv.scrollWidth
      setZoom(ratio < 1 ? Math.max(0.6, +ratio.toFixed(2)) : 1)
      // Yakınlaştırma uygulandıktan sonra şirket düğümü (üst orta) görünür olsun.
      window.requestAnimationFrame(() => {
        vp.scrollLeft = Math.max(0, (vp.scrollWidth - vp.clientWidth) / 2)
      })
    }, 60)
    return () => window.clearTimeout(t)
  }, [ready, layout, focus])

  const pan = useRef<{ x: number; y: number; left: number; top: number } | null>(null)
  const onPointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    // Yalnızca boş alandan tutunca kaydır; kart ve düğmeler kendi işini yapsın.
    if (e.button !== 0 || (e.target as HTMLElement).closest('[data-dept],button,a,input,[draggable="true"],.org-minimap')) return
    const vp = viewport.current!
    pan.current = { x: e.clientX, y: e.clientY, left: vp.scrollLeft, top: vp.scrollTop }
    vp.setPointerCapture(e.pointerId)
  }
  const onPointerMove = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (!pan.current) return
    const vp = viewport.current!
    vp.scrollLeft = pan.current.left - (e.clientX - pan.current.x)
    vp.scrollTop = pan.current.top - (e.clientY - pan.current.y)
  }
  const endPan = () => {
    pan.current = null
  }

  /** Sürüklerken kenara yaklaşınca tuval kendiliğinden kaysın. */
  const onViewportDragOver = (e: DragEvent<HTMLDivElement>) => {
    if (!drag) return
    const vp = viewport.current!
    const r = vp.getBoundingClientRect()
    const edge = 48
    const step = 18
    if (e.clientX < r.left + edge) vp.scrollLeft -= step
    else if (e.clientX > r.right - edge) vp.scrollLeft += step
    if (e.clientY < r.top + edge) vp.scrollTop -= step
    else if (e.clientY > r.bottom - edge) vp.scrollTop += step
  }

  // Küçük harita için görünür alan (kaydırma) izlenir.
  const [scroll, setScroll] = useState({ x: 0, y: 0, w: 0, h: 0 })
  const scrollRaf = useRef(0)
  const onViewportScroll = () => {
    cancelAnimationFrame(scrollRaf.current)
    scrollRaf.current = requestAnimationFrame(() => {
      const vp = viewport.current
      if (vp) setScroll({ x: vp.scrollLeft, y: vp.scrollTop, w: vp.clientWidth, h: vp.clientHeight })
    })
  }

  const roots = useMemo(() => visibleRoots(model, focus), [model, focus])
  const showMinimap = layout === 'vertical' && model.byId.size > MINIMAP_OVER
  const measured = useMeasuredBoxes(viewport, canvas, layout === 'vertical' && ready && (url.matrix || showMinimap), [
    model,
    collapsed,
    focus,
    openSet,
    showAll,
    zoom,
    url.matrix,
    matrix.length,
  ])
  useEffect(() => {
    if (layout === 'vertical' && ready) onViewportScroll()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [layout, ready, zoom])

  /* ------------------------------ klavye gezintisi (dikey) ------------------------------ */
  const pendingFocus = useRef<string | null>(null)
  useEffect(() => {
    const id = pendingFocus.current
    if (!id) return
    pendingFocus.current = null
    const el = document.getElementById(`org-dept-btn-${id}`)
    el?.focus({ preventScroll: true })
    el?.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: reducedMotion() ? 'auto' : 'smooth' })
  })
  const onDeptKey = (e: KeyboardEvent<HTMLButtonElement>, deptId: string) => {
    if (e.key === 'f' || e.key === 'F') {
      e.preventDefault()
      focusOn(deptId)
      return
    }
    const r = navKey(e.key, deptId, roots, collapsed, 'down')
    if (!r) return
    e.preventDefault()
    if (r.toggle) toggleKids(r.toggle)
    if (r.move) {
      pendingFocus.current = r.move
      // Odak taşınmazsa (aynı render) effect yine de çalışsın.
      setScroll((s) => ({ ...s }))
    }
  }

  /* ------------------------------------ dışa aktarma ------------------------------------ */
  const [exporting, setExporting] = useState(false)
  const exportAs = async (fmt: 'png' | 'svg' | 'pdf') => {
    setExporting(true)
    try {
      const [{ computeLayout }, ex] = await Promise.all([import('./orgLayouts'), import('./orgExport')])
      // 3B görünüm dışa aktarılırken radyal ağaç (aynı x/z düzlemi) kullanılır.
      const exportKind = layout === 'layers3d' ? 'radial' : layout
      const lay = computeLayout(model, exportKind, { focus, collapsed, rootLabel: companyName, peopleKnown: canSeePeople })
      const title = [companyName, focus ? model.byId.get(focus)?.dept.name : null, formatDate(refDay), scenario ? tx('Senaryo: {0}', [scenario.name]) : null].filter(Boolean).join(' · ')
      const svg = ex.layoutToSvg({
        layout: lay,
        title,
        colorOf,
        subtitleOf,
        matrix: url.matrix ? matrix.map((m) => ({ from: m.fromDepartmentId, to: m.toDepartmentId, kind: m.kind })) : [],
        path,
      })
      const base = `${ex.fileSlug(companyName)}-${exportKind}${date ? `-${date}` : ''}`
      if (fmt === 'svg') ex.downloadSvg(svg, base)
      else if (fmt === 'png') await ex.downloadPng(svg, base)
      else await ex.printSvg(svg)
    } catch (e) {
      toast.stop(tx('Dışa aktarılamadı. {0}', [errorText(e)]))
    } finally {
      setExporting(false)
    }
  }

  const copyLink = () => {
    const params = applyViewParams(searchParams, { ...url, zoom })
    const href = `${window.location.origin}${window.location.pathname}?${params.toString()}`
    navigator.clipboard.writeText(href).then(
      () => toast.ok(tx('Bağlantı kopyalandı; açan kişi aynı görünümü görür (yetkisi kadarını).')),
      () => toast.stop(tx('Kopyalanamadı')),
    )
  }

  const present = () => {
    const back = `${window.location.pathname}${window.location.search}`
    routerNavigate(`/panel/organizasyon/sunum?${new URLSearchParams({ ...(focus ? { odak: focus } : {}), donus: back }).toString()}`)
  }

  /* ------------------------------------ çizim ------------------------------------ */

  if (departments.isPending || (canSeePeople && employees.isPending)) {
    return (
      <Panel>
        <RowsSkeleton rows={6} columns={4} />
      </Panel>
    )
  }
  if (departments.isError) {
    return (
      <Panel>
        <ErrorState message={errorText(departments.error)} onRetry={() => void departments.refetch()} />
      </Panel>
    )
  }
  if (model.roots.length === 0) {
    return (
      <Panel>
        <EmptyState icon={Building2} title={tx('Henüz departman yok')} detail={tx('Şemayı görmek için önce bir departman ekleyin.')} />
      </Panel>
    )
  }

  const ctx: ChartCtx = {
    model,
    peopleKnown: canSeePeople,
    canMove: canMoveNow,
    drag,
    setDrag,
    overId,
    setOverId,
    dropOn,
    openMove: setMoving,
    pending,
    highlight,
    nameOf,
    isOpen,
    toggleOpen,
    showAll,
    toggleShowAll,
    collapsed,
    toggleKids,
    colorOf,
    personMark,
    path,
    selectedDept,
    selectedPerson,
    deptMatches,
    select,
    focusOn,
    onDeptKey,
    pulse: pulse.ids,
    pulseSeq: pulse.seq,
  }

  const movingPerson = moving
    ? ([...model.byId.values()].flatMap((n) => n.members).find((p) => p.id === moving) ??
      model.unassigned.find((p) => p.id === moving))
    : undefined

  const focusChain = focus ? ancestorsOf(model, focus) : []
  const selectedChain = selectedDept ? ancestorsOf(model, selectedDept) : []
  const svgKind = layout === 'horizontal' || layout === 'radial' || layout === 'sunburst' || layout === 'treemap' ? layout : null
  const sideBySide = Boolean(diff && draftModel && compare === 'side')

  const svgView = (
    kind: SvgLayoutKind, m: ChartModel, z: number, onZoom: (z: number) => void,
    paint: { colorOf: (id: string) => string; subtitleOf: (id: string) => string } = { colorOf, subtitleOf },
  ) => (
    <Suspense fallback={<div className="p-4"><RowsSkeleton rows={6} columns={4} /></div>}>
      <OrgSvgView
        model={m}
        kind={kind}
        rootLabel={companyName}
        focus={focus && m.byId.has(focus) ? focus : null}
        collapsed={collapsed}
        peopleKnown={canSeePeople}
        colorOf={paint.colorOf}
        subtitleOf={paint.subtitleOf}
        path={path}
        matches={deptMatches}
        selectedDept={selectedDept}
        matrix={url.matrix ? matrix : null}
        zoom={z}
        zoomFromUrl={initial.current.zoomFromUrl && initial.current.layout === layout}
        onZoom={onZoom}
        onToggle={toggleKids}
        onSelect={(id) => select(`d:${id}`)}
        onFocus={(id) => focusOn(id)}
        centerRequest={centerRequest}
        fitRequest={fitRequest}
        pulse={pulse.ids}
        pulseSeq={pulse.seq}
      />
    </Suspense>
  )

  const outline = (
    <OrgOutline
      roots={roots}
      collapsed={collapsed}
      onToggle={toggleKids}
      selectedDept={selectedDept}
      path={path}
      matches={deptMatches}
      colorOf={colorOf}
      subtitleOf={subtitleOf}
      headOf={(id) => {
        const h = model.byId.get(id)?.dept.headEmployeeId
        return h ? nameOf(h) : null
      }}
      linkCount={url.matrix ? linkCount : () => 0}
      onSelect={(id) => select(`d:${id}`)}
      onFocus={(id) => focusOn(id)}
    />
  )

  const matrixSegments =
    layout === 'vertical' && url.matrix
      ? matrix
          .map((m) => {
            const a = measured.rects.get(m.fromDepartmentId)
            const b = measured.rects.get(m.toDepartmentId)
            return a && b ? { m, d: matrixCurve(a, b) } : null
          })
          .filter((x): x is { m: DepartmentLink; d: string } => x !== null)
      : []

  return (
    <Ctx.Provider value={ctx}>
      <div className="org-chart-root space-y-3">
        {!canSeePeople ? (
          <InfoNote>{tx('Şema yalnızca departmanları gösteriyor. Çalışanları görmek için yönetici yetkisi gerekir.')}</InfoNote>
        ) : date ? (
          <InfoNote>{tx('Şema {0} tarihindeki görevlendirmelere göre gösteriliyor ve salt okunur. Departman geçmişi tutulmadığından silinmiş departmanlar görünmez; üst departman bağları bugünkü hâliyle çizilir.', [formatDate(date)])}</InfoNote>
        ) : diff ? (
          <InfoNote>{tx('Senaryo karşılaştırması: renkler senaryodaki kişi sayısı değişimini gösterir. Senaryo gerçek organizasyonu değiştirmez.')}</InfoNote>
        ) : canMove && layout === 'vertical' ? (
          <InfoNote>{tx('Bir çalışan kartını tutup başka bir departman kutusuna bırakın; atama bugünden itibaren değişir. Tarih ya da unvan değiştirmek, klavye veya dokunmatik ekranla taşımak için kartın üzerindeki', [])}{' '}
            <ArrowRightLeft className="inline size-3.5 align-[-2px]" aria-label={tx('taşı')} />{' '}{tx('düğmesini kullanın.')}
          </InfoNote>
        ) : !canMove ? (
          <InfoNote>{tx('Departman değişikliğini yalnızca İK yöneticisi yapabilir; şema sizin için salt okunur.')}</InfoNote>
        ) : null}

        {model.cycleCount > 0 && (
          <p role="alert" className="flex items-start gap-2 rounded-lg border border-[hsl(var(--warning))]/35 bg-[hsl(var(--warning))]/8 px-3 py-2 text-[13px]">
            <TriangleAlert className="mt-0.5 size-4 shrink-0 text-[hsl(var(--warning))]" aria-hidden />
            {tx('{0} departmanın üst departman zinciri kendi içinde döngüye giriyor; bu departmanlar kök seviyede gösterildi. Üst departman bilgisini düzeltmek gerekir.', [model.cycleCount])}</p>
        )}

        <div className="overflow-hidden rounded-xl border border-border bg-card">
          {/* Araç çubuğu */}
          <div className="org-toolbar space-y-2 border-b border-border p-3">
            <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
              <OrgSearch query={query} setQuery={setQuery} hits={hits} matchCount={highlight.size + deptMatches.size} peopleKnown={canSeePeople} onPick={goTo} />
              <div className="flex flex-wrap items-center gap-2">
                <span className="tabular text-[12px] text-muted-foreground">
                  {formatNumber(model.byId.size)}{' '}{tx('departman')}
                  {canSeePeople && tx(' · {0} kişi', [formatNumber(personCount)])}
                </span>
                {layout !== 'list' && (
                  <div className="flex items-center rounded-md border border-border">
                    <Button size="icon-sm" variant="ghost" onClick={() => setZoom((z) => Math.max(layout === 'vertical' ? 0.4 : 0.1, +(z * (layout === 'vertical' ? 1 : 0.85) - (layout === 'vertical' ? 0.1 : 0)).toFixed(2)))} aria-label={tx('Uzaklaştır')}>
                      <Minus aria-hidden />
                    </Button>
                    <span className="tabular w-11 text-center text-[12px] font-medium" aria-live="polite">%{Math.round(zoom * 100)}</span>
                    <Button size="icon-sm" variant="ghost" onClick={() => setZoom((z) => Math.min(layout === 'vertical' ? 1.5 : 4, +(z * (layout === 'vertical' ? 1 : 1.18) + (layout === 'vertical' ? 0.1 : 0)).toFixed(2)))} aria-label={tx('Yakınlaştır')}>
                      <Plus aria-hidden />
                    </Button>
                    <Button size="icon-sm" variant="ghost" onClick={fit} aria-label={tx('Ekrana sığdır')} title={tx('Ekrana sığdır')}>
                      <Maximize2 aria-hidden />
                    </Button>
                  </div>
                )}
              </div>
            </div>

            <div className="flex flex-wrap items-center gap-2">
              <Select value={layout} onValueChange={(v) => update({ layout: v as OrgLayoutKind })}>
                <SelectTrigger size="sm" aria-label={tx('Yerleşim')} className="min-w-36">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {(['vertical', 'horizontal', 'radial', 'sunburst', 'treemap', 'list', 'layers3d'] as OrgLayoutKind[]).map((k) => (
                    <SelectItem key={k} value={k}>
                      {layoutLabel(k)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <Select value={encoding} onValueChange={(v) => update({ encoding: v as OrgEncoding })} disabled={Boolean(diff)}>
                <SelectTrigger size="sm" aria-label={tx('Renklendirme')} className="min-w-36">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="department">{encodingLabel('department')}</SelectItem>
                  {canSeePeople && <SelectItem value="tenure">{encodingLabel('tenure')}</SelectItem>}
                  {canSeePeople && !date && <SelectItem value="leave">{encodingLabel('leave')}</SelectItem>}
                </SelectContent>
              </Select>
              <Button size="sm" variant={url.matrix ? 'secondary' : 'outline'} aria-pressed={url.matrix} onClick={() => update({ matrix: !url.matrix })}>
                <Waypoints aria-hidden />
                {tx('Matris bağları')}
              </Button>
              {canTime && !diff && (
                <Button
                  size="sm"
                  variant={date || timeOpen ? 'secondary' : 'outline'}
                  aria-pressed={Boolean(date || timeOpen)}
                  aria-expanded={Boolean(date || timeOpen)}
                  onClick={() => {
                    if (date || timeOpen) {
                      setTimeOpen(false)
                      setExtra({ [DATE_PARAM]: null })
                    } else setTimeOpen(true)
                  }}
                >
                  <History aria-hidden />
                  {tx('Zaman')}
                </Button>
              )}
              {canScenario && !date && (scenarios.data?.length ?? 0) > 0 && (
                <Select
                  value={scenarioId ?? '__none__'}
                  onValueChange={(v) => setExtra({ [SCENARIO_PARAM]: v === '__none__' ? null : v, ...(v === '__none__' ? { [COMPARE_PARAM]: null } : {}) })}
                >
                  <SelectTrigger size="sm" aria-label={tx('Senaryo ile karşılaştır')} className="min-w-44">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="__none__">{tx('Senaryo karşılaştırma yok')}</SelectItem>
                    {scenarios.data!.map((sc) => (
                      <SelectItem key={sc.id} value={sc.id}>
                        {tx('Senaryo: {0}', [sc.name])}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              )}
              {diff && (
                <div className="flex items-center rounded-md border border-border p-0.5" role="group" aria-label={tx('Karşılaştırma biçimi')}>
                  <Button size="xs" variant={compare === 'overlay' ? 'secondary' : 'ghost'} aria-pressed={compare === 'overlay'} onClick={() => setExtra({ [COMPARE_PARAM]: null })}>
                    {tx('Üst üste')}
                  </Button>
                  <Button size="xs" variant={compare === 'side' ? 'secondary' : 'ghost'} aria-pressed={compare === 'side'} onClick={() => setExtra({ [COMPARE_PARAM]: 'yan' })}>
                    {tx('Yan yana')}
                  </Button>
                </div>
              )}
              {canManageLinks && (
                <Button size="sm" variant="ghost" onClick={() => setLinksOpen(true)}>
                  {tx('Bağları yönet')}
                </Button>
              )}
              {canSeePeople && layout === 'vertical' && !sideBySide && (
                <>
                  <Button size="sm" variant="outline" onClick={() => setOpenSet(new Set(model.byId.keys()))}>
                    {tx('Tümünü aç')}
                  </Button>
                  <Button size="sm" variant="outline" onClick={() => setOpenSet(new Set())}>
                    {tx('Özet')}
                  </Button>
                </>
              )}
              <div className="flex flex-wrap items-center gap-2 sm:ml-auto">
                <DropdownMenu>
                  <DropdownMenuTrigger asChild>
                    <Button size="sm" variant="outline" disabled={exporting}>
                      {exporting ? <LoaderCircle className="animate-spin" aria-hidden /> : <Download aria-hidden />}
                      {tx('Dışa aktar')}
                    </Button>
                  </DropdownMenuTrigger>
                  <DropdownMenuContent align="end">
                    <DropdownMenuItem onSelect={() => void exportAs('png')}>{tx('PNG görsel')}</DropdownMenuItem>
                    <DropdownMenuItem onSelect={() => void exportAs('svg')}>{tx('SVG (vektör)')}</DropdownMenuItem>
                    <DropdownMenuItem onSelect={() => void exportAs('pdf')}>{tx('PDF (yazdır)')}</DropdownMenuItem>
                  </DropdownMenuContent>
                </DropdownMenu>
                <Button size="sm" variant="outline" onClick={copyLink}>
                  <Link2 aria-hidden />
                  {tx('Bağlantıyı kopyala')}
                </Button>
                <Button size="sm" variant="ghost" onClick={present}>
                  <Presentation aria-hidden />
                  {tx('Sunum')}
                </Button>
              </div>
            </div>

            {(date || timeOpen) && canTime && !diff && (
              <OrgTimeSlider
                stops={stops}
                date={date}
                onChange={(d) => setExtra({ [DATE_PARAM]: d })}
                loading={snap.isFetching}
                error={snap.isError}
                summary={
                  date && snap.data
                    ? tx('O tarihte {0} kişi, {1} departman; bugün {2} kişi.', [
                        formatNumber(model.deptOf.size),
                        formatNumber(model.byId.size),
                        formatNumber(liveModel.deptOf.size),
                      ])
                    : null
                }
              />
            )}

            {diff && scenario && (
              <p className="text-[12.5px]" aria-live="polite">
                <span className="font-medium">{scenario.name}</span>
                <span className="text-muted-foreground">
                  {' · '}
                  {tx('{0} kişi taşınıyor, {1} yeni pozisyon, {2} ayrılış; {3} departman etkileniyor.', [diff.moved, diff.hires, diff.exits, diff.affected])}
                </span>{' '}
                <Link className="font-medium text-primary hover:underline" to="/panel/org-senaryolari">
                  {tx('Senaryoyu düzenle')}
                </Link>
              </p>
            )}

            {/* Odak kırıntısı */}
            {focus && (
              <nav aria-label={tx('Odak yolu')} className="flex flex-wrap items-center gap-1 text-[12.5px]">
                <span className="text-muted-foreground">{tx('Odak:')}</span>
                <button type="button" className="rounded px-1 font-medium text-primary hover:underline" onClick={() => focusOn(null)}>
                  {companyName}
                </button>
                {focusChain.map((id, i) => (
                  <span key={id} className="flex items-center gap-1">
                    <ChevronRight className="size-3.5 text-muted-foreground" aria-hidden />
                    {i === focusChain.length - 1 ? (
                      <span aria-current="page" className="font-semibold">{model.byId.get(id)?.dept.name}</span>
                    ) : (
                      <button type="button" className="rounded px-1 font-medium text-primary hover:underline" onClick={() => focusOn(id)}>
                        {model.byId.get(id)?.dept.name}
                      </button>
                    )}
                  </span>
                ))}
                <Button size="xs" variant="ghost" onClick={() => focusOn(null)}>
                  <X aria-hidden />
                  {tx('Odaktan çık')}
                </Button>
              </nav>
            )}

            {/* Seçili yol */}
            {selectedChain.length > 0 && (
              <p className="flex flex-wrap items-center gap-1 text-[12.5px]" aria-live="polite">
                <span className="text-muted-foreground">{tx('Köke giden yol:')}</span>
                <span className="font-medium">{companyName}</span>
                {selectedChain.map((id) => (
                  <span key={id} className="flex items-center gap-1">
                    <ChevronRight className="size-3.5 text-muted-foreground" aria-hidden />
                    <span className="font-medium">{model.byId.get(id)?.dept.name}</span>
                  </span>
                ))}
                {selectedPerson && (
                  <span className="flex items-center gap-1">
                    <ChevronRight className="size-3.5 text-muted-foreground" aria-hidden />
                    <span className="font-semibold text-primary">{nameOf(selectedPerson)}</span>
                  </span>
                )}
                <Button size="icon-xs" variant="ghost" onClick={() => update({ selected: null })} aria-label={tx('Seçimi temizle')}>
                  <X aria-hidden />
                </Button>
              </p>
            )}

            {/* Lejant */}
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[11.5px] text-muted-foreground" aria-label={tx('Lejant')} role="group">
              {legend.map((l) => (
                <span key={l.key} className="flex items-center gap-1.5">
                  <span aria-hidden className="size-2.5 rounded-sm" style={{ background: l.color }} />
                  {l.label}
                </span>
              ))}
              {encoding === 'department' && model.roots.length > 8 && <span>{tx('…ve {0} kök departman daha', [model.roots.length - 8])}</span>}
              {url.matrix && (
                <>
                  <span className="flex items-center gap-1.5">
                    <svg width="22" height="6" aria-hidden><line x1="0" y1="3" x2="22" y2="3" className="org-matrix" strokeWidth="2" /></svg>
                    {tx('Fonksiyonel bağ')}
                  </span>
                  <span className="flex items-center gap-1.5">
                    <svg width="22" height="6" aria-hidden><line x1="0" y1="3" x2="22" y2="3" className="org-matrix org-matrix-project" strokeWidth="2" /></svg>
                    {tx('Proje bağı')}
                  </span>
                  {links.isError && <span className="text-destructive">{tx('Matris bağları okunamadı.')}</span>}
                  {links.isSuccess && matrix.length === 0 && <span>{tx('Bu şirkette matris bağı yok.')}</span>}
                </>
              )}
              {encoding === 'leave' && (
                <span>
                  {leave.isPending
                    ? tx('İzin bilgisi yükleniyor…')
                    : leave.isError
                      ? tx('İzin bilgisi okunamadı.')
                      : isHr
                        ? tx('İzin türü gösterilmez.')
                        : tx('Yalnızca başı olduğunuz departmanların izin durumu gösterilir; izin türü gösterilmez.')}
                </span>
              )}
            </div>
          </div>

          {/* Tuval */}
          {sideBySide && draftModel ? (
            // Yan yana: solda bugün, sağda senaryo; aynı yerleşim (dikey/liste/3B için yatay ağaç).
            <div className="grid divide-y divide-border lg:grid-cols-2 lg:divide-x lg:divide-y-0">
              <section aria-label={tx('Bugünkü yapı')}>
                <p className="border-b border-border px-3 py-1.5 text-[12px] font-semibold">{tx('Bugün')}</p>
                {svgView(svgKind ?? 'horizontal', liveModel, zoom, setZoom, { colorOf: liveColorOf, subtitleOf: liveSubtitleOf })}
              </section>
              <section aria-label={tx('Senaryodaki yapı')}>
                <p className="border-b border-border px-3 py-1.5 text-[12px] font-semibold">{tx('Senaryo: {0}', [scenario?.name ?? ''])}</p>
                {svgView(svgKind ?? 'horizontal', draftModel, zoomRight, setZoomRight)}
              </section>
            </div>
          ) : layout === 'layers3d' ? (
            <ThreeDGate fallback={svgView('radial', model, zoom, setZoom)}>
              <Org3DView
                model={model}
                rootLabel={companyName}
                focus={focus}
                collapsed={collapsed}
                peopleKnown={canSeePeople}
                colorOf={colorOf}
                subtitleOf={subtitleOf}
                path={path}
                matches={deptMatches}
                selectedDept={selectedDept}
                matrix={url.matrix ? matrix : null}
                zoom={zoom}
                onSelect={(id) => select(`d:${id}`)}
                onFocus={(id) => focusOn(id)}
                onToggle={toggleKids}
                centerRequest={centerRequest}
                fitRequest={fitRequest}
                pulse={pulse.ids}
                pulseSeq={pulse.seq}
                aside={outline}
              />
            </ThreeDGate>
          ) : svgKind ? (
            svgView(svgKind, model, zoom, setZoom)
          ) : layout === 'list' ? (
            outline
          ) : (
            <div
              ref={viewport}
              onPointerDown={onPointerDown}
              onPointerMove={onPointerMove}
              onPointerUp={endPan}
              onPointerCancel={endPan}
              onDragOver={onViewportDragOver}
              onScroll={onViewportScroll}
              className="relative max-h-[72vh] min-h-[420px] cursor-grab overflow-auto bg-[radial-gradient(hsl(var(--border))_1px,transparent_1px)] [background-size:18px_18px] active:cursor-grabbing"
            >
              {/* Matris eğrileri kutuların ARKASINDA kalır (kutuların üstünü çizmesin). */}
              <div ref={canvas} className="dept-chart relative z-[1] w-max min-w-full p-6" style={{ zoom }}>
                <div className="flex flex-col items-center">
                  {focus ? (
                    <div className="mb-1 h-0" aria-hidden />
                  ) : (
                    <div className="flex items-center gap-2 rounded-xl border border-border bg-card px-4 py-2.5 shadow-sm">
                      <span className="flex size-8 items-center justify-center rounded-lg bg-primary/10 text-primary">
                        <Building2 className="size-4" aria-hidden />
                      </span>
                      <span>
                        <span className="block text-[14px] font-semibold">{companyName}</span>
                        <span className="tabular block text-[11px] text-muted-foreground">
                          {tx('{0} kök departman', [model.roots.length])}</span>
                      </span>
                    </div>
                  )}
                  <ul className={focus ? 'dept-root dept-root-focus' : 'dept-root'}>
                    {roots.map((r) => (
                      <Branch key={r.dept.id} node={r} />
                    ))}
                  </ul>
                </div>
              </div>
              {matrixSegments.length > 0 && (
                <svg
                  aria-hidden
                  // Bağlantı çizgileri ve kartların üstünde (z-[3]); tıklamaları engellemez.
                  className="pointer-events-none absolute top-0 left-0 z-[3]"
                  width={measured.size.w}
                  height={measured.size.h}
                >
                  <defs>
                    <marker id="org-v-arrow-f" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto">
                      <path d="M0,0L10,5L0,10z" className="org-matrix-head" />
                    </marker>
                    <marker id="org-v-arrow-p" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto">
                      <path d="M0,0L10,5L0,10z" className="org-matrix-head org-matrix-head-project" />
                    </marker>
                  </defs>
                  {matrixSegments.map(({ m, d }) => (
                    <g key={m.id}>
                      <path d={d} className="org-matrix-halo" strokeWidth={6} />
                      <path
                        d={d}
                        className={m.kind === 'Project' ? 'org-matrix org-matrix-project' : 'org-matrix'}
                        strokeWidth={2.5}
                        markerEnd={`url(#org-v-arrow-${m.kind === 'Project' ? 'p' : 'f'})`}
                      />
                    </g>
                  ))}
                </svg>
              )}
            </div>
          )}
          {showMinimap && layout === 'vertical' && measured.rects.size > 0 && (
            <div className="relative h-0">
              <OrgMinimap
                items={[...measured.rects].map(([id, r]) => ({ id, ...r, color: colorOf(id) }))}
                bounds={{ x0: 0, y0: 0, x1: measured.size.w, y1: measured.size.h }}
                view={{ x: scroll.x, y: scroll.y, w: scroll.w, h: scroll.h }}
                highlight={path}
                onJump={(cx, cy) => {
                  const vp = viewport.current
                  if (!vp) return
                  vp.scrollTo({ left: cx - vp.clientWidth / 2, top: cy - vp.clientHeight / 2 })
                }}
              />
            </div>
          )}
        </div>

        {canSeePeople && model.unassigned.length > 0 && (
          <div className="rounded-xl border border-dashed border-border bg-card p-3">
            <p className="mb-2 flex items-center gap-1.5 text-[13px] font-semibold">
              <UserRoundX className="size-4 text-muted-foreground" aria-hidden />
              {tx('Atanmamış')}
              <span className="tabular font-normal text-muted-foreground">{model.unassigned.length}</span>
            </p>
            <p className="mb-2.5 text-[12px] text-muted-foreground">{tx('Aktif departman ataması olmayan çalışanlar{0}', [canMove ? tx('. Bir departman kutusuna sürükleyerek atayabilirsiniz.') : '.'])}
            </p>
            <div className="grid gap-1.5 sm:grid-cols-2 lg:grid-cols-4">
              {model.unassigned.map((p) => (
                <PersonCard key={p.id} person={p} deptId={null} isHead={false} />
              ))}
            </div>
          </div>
        )}
      </div>

      {moving && movingPerson && (
        <MoveEmployeeModal
          name={movingPerson.name}
          currentDeptId={model.deptOf.get(moving) ?? null}
          currentTitle={currentTitle(moving)}
          currentFrom={currentActive(moving)?.effectiveFrom.slice(0, 10) ?? null}
          options={[...model.byId.keys()].map((id) => ({ value: id, label: pathOf(id) })).sort((a, b) => a.label.localeCompare(b.label, 'tr-TR'))}
          defaultDate={today}
          onClose={() => setMoving(null)}
          onSubmit={(input) => {
            performMove(moving, input)
            setMoving(null)
          }}
        />
      )}

      {canManageLinks && (
        <DepartmentLinksModal
          open={linksOpen}
          onClose={() => setLinksOpen(false)}
          companyId={companyId}
          departments={departments.data ?? []}
          fromId={selectedDept}
        />
      )}
    </Ctx.Provider>
  )
}
