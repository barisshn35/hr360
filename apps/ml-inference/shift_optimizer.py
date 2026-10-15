"""
Vardiya planı optimizasyonu (madde 44): OR-Tools CP-SAT, çözücü yoksa / süre aşılırsa açgözlü sezgisel.

  POST /shift/optimize - talep (gün x vardiya x kişi sayısı, isteğe bağlı beceri), çalışanların müsaitliği,
                         tercihleri ve becerileri ile bir ÖNERİ üretir. Kayıt yazmaz; öneriyi İK/yönetici
                         timeshift-service "Vardiya planı > Öneri" ekranında farkıyla görür ve elle uygular.

Kesin kurallar (kiracı ayarı; varsayılanlar 4857 s. İş Kanunu ve Çalışma Süreleri Yönetmeliği):
  * çalışan günde en çok bir vardiya; izinli/tatil günü (unavailable) atanmaz; beceri istenen talebe yalnızca
    o beceriye sahip kişi sayılır
  * iki vardiya arası en az min_rest_hours (11) saat dinlenme (önceki haftanın son vardiyaları "history" ile)
  * ISO haftası başına net en çok weekly_max_hours (45) saat
  * günlük net süresi daily_max_hours (11) üstündeki ya da gece çalışması olup (yarısından çoğu 20:00-06:00)
    net süresi night_max_hours (7,5) üstündeki vardiya tanımı hiç kullanılmaz (yanıtta "unusable_shifts")
  * en çok max_consecutive_days (6) gün üst üste çalışma
Yumuşak hedefler (ağırlıklı toplam en küçüklenir):
  * karşılanmayan talep (1000 / kişi) - talebi karşılamak önceliklidir
  * tercihler: "müsait değil" günü 40, istemediği vardiya türü 15, tercih dışı gün/tür 3,
    haftalık gece tercihi aşımı 10 / gece
  * adalet: kişiler arası gece, hafta sonu ve toplam vardiya sayısı farkı (en çok - en az) 6 / 6 / 4

KVKK: istekte ad, çalışan kimliği ya da sağlık bilgisi yoktur; çalışanlar kiracıya özgü takma adlarla gelir
(timeshift-service üretir). Girdi saklanmaz. Otomatik uygulama yoktur (insan onayı).
"""

from __future__ import annotations

import time
from collections import defaultdict
from datetime import date, datetime, timedelta

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, ConfigDict, Field
from tr_text import tr_num

try:  # pragma: no cover - imajda kurulu; yoksa sezgisel kullanılır
    from ortools.sat.python import cp_model
    HAS_ORTOOLS = True
except Exception:  # noqa: BLE001
    cp_model = None
    HAS_ORTOOLS = False

router = APIRouter(prefix="/shift", tags=["shift-optimizer"])

MAX_EMPLOYEES = 200
MAX_DAYS = 31
MAX_TIME_LIMIT = 10.0

W_UNCOVERED = 1000
W_UNAVAILABLE_PREF = 40
W_AVOID_TYPE = 15
W_SOFT = 3
W_NIGHTS_OVER = 10
W_FAIR_NIGHT = 6
W_FAIR_WEEKEND = 6
W_FAIR_LOAD = 4


class ShiftDef(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str = Field(min_length=1, max_length=64)
    start_min: int = Field(ge=0, lt=1440)
    end_min: int = Field(ge=0, lt=1440)
    break_min: int = Field(default=0, ge=0, le=240)


class HistoryItem(BaseModel):
    model_config = ConfigDict(extra="forbid")
    date: date
    shift: str


class Employee(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id: str = Field(min_length=1, max_length=64)
    unavailable: list[date] = Field(default_factory=list, max_length=400)
    preferred_days: list[int] = Field(default_factory=list, max_length=7)
    unavailable_weekdays: list[int] = Field(default_factory=list, max_length=7)
    preferred_types: list[str] = Field(default_factory=list, max_length=2)
    avoid_types: list[str] = Field(default_factory=list, max_length=2)
    max_nights_per_week: int | None = Field(default=None, ge=0, le=7)
    skills: list[str] = Field(default_factory=list, max_length=20)
    history: list[HistoryItem] = Field(default_factory=list, max_length=60)


class Demand(BaseModel):
    model_config = ConfigDict(extra="forbid")
    date: date
    shift: str
    required: int = Field(ge=0, le=500)
    skill: str | None = Field(default=None, max_length=100)


class Rules(BaseModel):
    model_config = ConfigDict(extra="forbid")
    min_rest_hours: float = Field(default=11, ge=0, le=24)
    weekly_max_hours: float = Field(default=45, gt=0, le=80)
    daily_max_hours: float = Field(default=11, gt=0, le=24)
    night_max_hours: float = Field(default=7.5, gt=0, le=24)
    max_consecutive_days: int = Field(default=6, ge=1, le=14)


class OptimizeRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    days: list[date] = Field(min_length=1, max_length=MAX_DAYS)
    shifts: list[ShiftDef] = Field(min_length=1, max_length=20)
    employees: list[Employee] = Field(min_length=1, max_length=MAX_EMPLOYEES)
    demand: list[Demand] = Field(min_length=1, max_length=5000)
    rules: Rules = Field(default_factory=Rules)
    time_limit_seconds: float = Field(default=MAX_TIME_LIMIT, gt=0, le=60)
    solver: str = Field(default="auto", pattern="^(auto|cp-sat|greedy)$")


# ------------------------------------------------------------------ zaman yardımcıları

def iso_day(d: date) -> int:
    return d.isoweekday()


def interval(d: date, s: ShiftDef) -> tuple[datetime, datetime]:
    start = datetime(d.year, d.month, d.day) + timedelta(minutes=s.start_min)
    end = datetime(d.year, d.month, d.day) + timedelta(minutes=s.end_min)
    if end <= start:
        end += timedelta(days=1)
    return start, end


def span_minutes(s: ShiftDef) -> int:
    m = s.end_min - s.start_min
    return m if m > 0 else m + 1440


def net_minutes(s: ShiftDef) -> int:
    return max(0, span_minutes(s) - s.break_min)


def night_minutes(s: ShiftDef) -> int:
    """20:00-06:00 dilimine düşen dakika (gece yarısını geçen vardiya dahil)."""
    total = 0
    start = s.start_min
    end = start + span_minutes(s)
    for base in (-1440, 0, 1440):
        ns, ne = base + 1200, base + 1440 + 360
        a, b = max(start, ns), min(end, ne)
        if b > a:
            total += b - a
    return total


def is_night(s: ShiftDef) -> bool:
    return night_minutes(s) * 2 >= span_minutes(s)


def gap_hours(a: tuple[datetime, datetime], b: tuple[datetime, datetime]) -> float:
    """İki aralık arası boşluk (saat); çakışıyorsa negatif."""
    if a[0] < b[1] and b[0] < a[1]:
        return -1.0
    g = b[0] - a[1] if b[0] >= a[1] else a[0] - b[1]
    return g.total_seconds() / 3600


def iso_week(d: date) -> tuple[int, int]:
    y, w, _ = d.isocalendar()
    return y, w


# ------------------------------------------------------------------ problem

class Problem:
    def __init__(self, req: OptimizeRequest):
        self.req = req
        self.days = sorted(set(req.days))
        self.shifts = {s.id: s for s in req.shifts}
        self.rules = req.rules
        self.unusable: dict[str, str] = {}
        for s in req.shifts:
            if net_minutes(s) > self.rules.daily_max_hours * 60:
                self.unusable[s.id] = "daily"
            elif is_night(s) and net_minutes(s) > self.rules.night_max_hours * 60:
                self.unusable[s.id] = "night"
        self.emps = req.employees
        self.unavail = {e.id: set(e.unavailable) for e in self.emps}
        self.skills = {e.id: set(e.skills) for e in self.emps}
        # Talep: (gün, vardiya) -> toplam; (gün, vardiya, beceri) -> en az
        self.total: dict[tuple[date, str], int] = defaultdict(int)
        self.skill_req: dict[tuple[date, str, str], int] = defaultdict(int)
        has_plain: set[tuple[date, str]] = set()
        day_set = set(self.days)
        for d in req.demand:
            if d.shift not in self.shifts or d.date not in day_set or d.required <= 0:
                continue
            if d.skill:
                self.skill_req[(d.date, d.shift, d.skill)] += d.required
            else:
                self.total[(d.date, d.shift)] += d.required
                has_plain.add((d.date, d.shift))
        for (d, s, _k), n in list(self.skill_req.items()):
            if (d, s) not in has_plain:
                self.total[(d, s)] = max(self.total[(d, s)], sum(v for (dd, ss, _), v in self.skill_req.items() if dd == d and ss == s))
        self.slots = sorted(((d, s) for (d, s), n in self.total.items() if n > 0 and s not in self.unusable),
                            key=lambda x: (x[0], self.shifts[x[1]].start_min))
        self.history = {e.id: [(h.date, h.shift) for h in e.history if h.shift in self.shifts] for e in self.emps}

    def allowed(self, e: Employee, d: date, s: str) -> bool:
        return d not in self.unavail[e.id] and s not in self.unusable

    def pref_cost(self, e: Employee, d: date, s: str) -> int:
        day = iso_day(d)
        typ = "Night" if is_night(self.shifts[s]) else "Day"
        c = 0
        if day in e.unavailable_weekdays:
            c += W_UNAVAILABLE_PREF
        elif e.preferred_days and day not in e.preferred_days:
            c += W_SOFT
        if typ in e.avoid_types:
            c += W_AVOID_TYPE
        elif e.preferred_types and typ not in e.preferred_types:
            c += W_SOFT
        return c

    def conflict(self, d1: date, s1: str, d2: date, s2: str) -> bool:
        """İki atama birlikte kurala aykırı mı (çakışma ya da dinlenme eksik)."""
        if d1 == d2:
            return True
        g = gap_hours(interval(d1, self.shifts[s1]), interval(d2, self.shifts[s2]))
        return g < self.rules.min_rest_hours


# ------------------------------------------------------------------ doğrulama (testler ve yanıt için)

def violations(p: Problem, assignments: list[dict]) -> list[str]:
    """Önerinin kesin kurallara aykırılıkları (boş liste = uygun)."""
    out: list[str] = []
    by_emp: dict[str, list[tuple[date, str]]] = defaultdict(list)
    for a in assignments:
        by_emp[a["employee"]].append((a["date"] if isinstance(a["date"], date) else date.fromisoformat(a["date"]), a["shift"]))
    emp_map = {e.id: e for e in p.emps}
    for eid, items in by_emp.items():
        e = emp_map[eid]
        days = [d for d, _ in items]
        if len(days) != len(set(days)):
            out.append(f"{eid}: aynı gün iki vardiya")
        for d, s in items:
            if not p.allowed(e, d, s):
                out.append(f"{eid}: {d} müsait değil ya da vardiya kullanılamaz")
        full = sorted(items + p.history[eid])
        for i in range(len(full)):
            for j in range(i + 1, len(full)):
                if (full[j][0] - full[i][0]).days > 2:
                    break
                if p.conflict(full[i][0], full[i][1], full[j][0], full[j][1]):
                    out.append(f"{eid}: {full[i][0]} ile {full[j][0]} arası dinlenme eksik")
        weeks: dict[tuple[int, int], int] = defaultdict(int)
        for d, s in full:
            weeks[iso_week(d)] += net_minutes(p.shifts[s])
        for wk, m in weeks.items():
            if wk in {iso_week(d) for d in p.days} and m > p.rules.weekly_max_hours * 60 + 1e-6:
                out.append(f"{eid}: {wk[1]}. hafta {tr_num(m / 60, 1)} saat")
        worked = sorted({d for d, _ in full})
        run = 1
        for i in range(1, len(worked)):
            run = run + 1 if (worked[i] - worked[i - 1]).days == 1 else 1
            if run > p.rules.max_consecutive_days:
                out.append(f"{eid}: {run} gün üst üste")
                break
    return out


def uncovered(p: Problem, assignments: list[dict]) -> list[dict]:
    count: dict[tuple[date, str], int] = defaultdict(int)
    skill_count: dict[tuple[date, str, str], int] = defaultdict(int)
    for a in assignments:
        d = a["date"] if isinstance(a["date"], date) else date.fromisoformat(a["date"])
        count[(d, a["shift"])] += 1
        for k in p.skills[a["employee"]]:
            skill_count[(d, a["shift"], k)] += 1
    out = []
    for (d, s), n in sorted(p.total.items()):
        if count[(d, s)] < n:
            out.append({"date": d.isoformat(), "shift": s, "skill": None, "missing": n - count[(d, s)]})
    for (d, s, k), n in sorted(p.skill_req.items()):
        if skill_count[(d, s, k)] < n:
            out.append({"date": d.isoformat(), "shift": s, "skill": k, "missing": n - skill_count[(d, s, k)]})
    return out


# ------------------------------------------------------------------ açgözlü sezgisel

def solve_greedy(p: Problem) -> list[dict]:
    """
    Talep dilimlerini gün ve başlangıç saatine göre dolaşır; her boş yer için kesin kurallara uyan, tercih
    maliyeti + yük (adalet) en düşük kişiyi seçer. Beceri istenen yerler önce doldurulur.
    """
    assigned: dict[str, list[tuple[date, str]]] = {e.id: list(p.history[e.id]) for e in p.emps}
    load: dict[str, int] = defaultdict(int)
    nights: dict[str, int] = defaultdict(int)
    weekends: dict[str, int] = defaultdict(int)
    out: list[dict] = []

    def feasible(e: Employee, d: date, s: str) -> bool:
        if not p.allowed(e, d, s):
            return False
        mine = assigned[e.id]
        if any(x == d for x, _ in mine):
            return False
        for x, xs in mine:
            if abs((x - d).days) <= 2 and p.conflict(x, xs, d, s):
                return False
        wk = iso_week(d)
        mins = sum(net_minutes(p.shifts[xs]) for x, xs in mine if iso_week(x) == wk) + net_minutes(p.shifts[s])
        if mins > p.rules.weekly_max_hours * 60:
            return False
        days = {x for x, _ in mine} | {d}
        run = 1
        k = d - timedelta(days=1)
        while k in days:
            run += 1
            k -= timedelta(days=1)
        k = d + timedelta(days=1)
        while k in days:
            run += 1
            k += timedelta(days=1)
        return run <= p.rules.max_consecutive_days

    def cost(e: Employee, d: date, s: str) -> float:
        c = p.pref_cost(e, d, s) + W_FAIR_LOAD * load[e.id]
        if is_night(p.shifts[s]):
            c += W_FAIR_NIGHT * nights[e.id]
            wk = iso_week(d)
            n_wk = sum(1 for x, xs in assigned[e.id] if iso_week(x) == wk and is_night(p.shifts[xs]))
            if e.max_nights_per_week is not None and n_wk + 1 > e.max_nights_per_week:
                c += W_NIGHTS_OVER
        if iso_day(d) >= 6:
            c += W_FAIR_WEEKEND * weekends[e.id]
        return c

    def take(e: Employee, d: date, s: str) -> None:
        assigned[e.id].append((d, s))
        load[e.id] += 1
        if is_night(p.shifts[s]):
            nights[e.id] += 1
        if iso_day(d) >= 6:
            weekends[e.id] += 1
        out.append({"employee": e.id, "date": d.isoformat(), "shift": s})

    for d, s in p.slots:
        filled: list[str] = []
        for (dd, ss, k), n in sorted(p.skill_req.items()):
            if dd != d or ss != s:
                continue
            have = sum(1 for eid in filled if k in p.skills[eid])
            for _ in range(n - have):
                if len(filled) >= p.total[(d, s)]:
                    break
                cands = [e for e in p.emps if k in p.skills[e.id] and e.id not in filled and feasible(e, d, s)]
                if not cands:
                    break
                best = min(cands, key=lambda e: (cost(e, d, s), e.id))
                take(best, d, s)
                filled.append(best.id)
        while len(filled) < p.total[(d, s)]:
            cands = [e for e in p.emps if e.id not in filled and feasible(e, d, s)]
            if not cands:
                break
            best = min(cands, key=lambda e: (cost(e, d, s), e.id))
            take(best, d, s)
            filled.append(best.id)
    return out


# ------------------------------------------------------------------ CP-SAT

def solve_cpsat(p: Problem, time_limit: float) -> tuple[list[dict] | None, str]:
    if not HAS_ORTOOLS:
        return None, "unavailable"
    m = cp_model.CpModel()
    x: dict[tuple[str, date, str], object] = {}
    for e in p.emps:
        for d, s in p.slots:
            if p.allowed(e, d, s):
                x[(e.id, d, s)] = m.NewBoolVar(f"x_{e.id}_{d}_{s}")
    if not x:
        return [], "OPTIMAL"
    by_ed: dict[tuple[str, date], list] = defaultdict(list)
    for (eid, d, s), v in x.items():
        by_ed[(eid, d)].append((s, v))

    # Günde en çok bir vardiya.
    for vs in by_ed.values():
        if len(vs) > 1:
            m.AddAtMostOne(v for _, v in vs)

    # Talep: üst sınır kesin, eksik karşılama yumuşak (ağır ceza).
    under = []
    for d, s in p.slots:
        vs = [x[(e.id, d, s)] for e in p.emps if (e.id, d, s) in x]
        n = p.total[(d, s)]
        m.Add(sum(vs) <= n)
        u = m.NewIntVar(0, n, f"u_{d}_{s}")
        m.Add(sum(vs) + u >= n)
        under.append(u)
    for (d, s, k), n in p.skill_req.items():
        if (d, s) not in p.total or s in p.unusable:
            continue
        vs = [x[(e.id, d, s)] for e in p.emps if k in p.skills[e.id] and (e.id, d, s) in x]
        u = m.NewIntVar(0, n, f"uk_{d}_{s}_{k}")
        m.Add(sum(vs) + u >= n)
        under.append(u)

    emp_ids = [e.id for e in p.emps]
    week_of_range = {iso_week(d) for d in p.days}
    for e in p.emps:
        hist = p.history[e.id]
        mine = [(d, s, v) for (eid, d, s), v in x.items() if eid == e.id]
        # Dinlenme: birlikte aykırı iki atama (2 gün içinde) aynı anda seçilemez; geçmişle aykırı olan seçilemez.
        for i in range(len(mine)):
            d1, s1, v1 = mine[i]
            for hd, hs in hist:
                if abs((hd - d1).days) <= 2 and p.conflict(hd, hs, d1, s1):
                    m.Add(v1 == 0)
            for j in range(i + 1, len(mine)):
                d2, s2, v2 = mine[j]
                if d1 == d2 or abs((d2 - d1).days) > 2:
                    continue
                if p.conflict(d1, s1, d2, s2):
                    m.AddBoolOr([v1.Not(), v2.Not()])
        # Haftalık süre.
        for wk in week_of_range:
            hist_min = sum(net_minutes(p.shifts[hs]) for hd, hs in hist if iso_week(hd) == wk)
            terms = [net_minutes(p.shifts[s]) * v for d, s, v in mine if iso_week(d) == wk]
            if terms:
                m.Add(sum(terms) + hist_min <= int(p.rules.weekly_max_hours * 60))
        # Ardışık gün: her (max+1) günlük pencerede en çok max gün.
        k = p.rules.max_consecutive_days
        hist_days = {hd for hd, _ in hist}
        if p.days:
            first = min(p.days) - timedelta(days=k)
            last = max(p.days)
            cur = first
            while cur + timedelta(days=k) <= last:
                window = [cur + timedelta(days=i) for i in range(k + 1)]
                terms = []
                fixed = 0
                for wd in window:
                    if wd in hist_days:
                        fixed += 1
                    terms.extend(v for _, v in by_ed.get((e.id, wd), []))
                if terms and fixed + len({wd for wd in window if by_ed.get((e.id, wd))}) > k:
                    m.Add(sum(terms) + fixed <= k)
                cur += timedelta(days=1)

    # Tercih maliyeti ve haftalık gece tercihi.
    pref_terms = []
    emp_by_id = {e.id: e for e in p.emps}
    for (eid, d, s), v in x.items():
        c = p.pref_cost(emp_by_id[eid], d, s)
        if c:
            pref_terms.append(c * v)
    for e in p.emps:
        if e.max_nights_per_week is None:
            continue
        for wk in week_of_range:
            nv = [v for (eid, d, s), v in x.items() if eid == e.id and iso_week(d) == wk and is_night(p.shifts[s])]
            if len(nv) > e.max_nights_per_week:
                over = m.NewIntVar(0, len(nv), f"no_{e.id}_{wk}")
                m.Add(over >= sum(nv) - e.max_nights_per_week)
                pref_terms.append(W_NIGHTS_OVER * over)

    # Adalet: gece, hafta sonu ve toplam yükte en çok - en az farkı.
    def spread(name: str, pick) -> object | None:
        counts = []
        # Hiç atanamayan (tüm dönem izinli) kişi adalet farkına katılmaz.
        for eid in emp_ids:
            if not any(e2 == eid for (e2, _d, _s) in x):
                continue
            vs = [v for (e2, d, s), v in x.items() if e2 == eid and pick(d, s)]
            c = m.NewIntVar(0, len(p.days), f"{name}_{eid}")
            if vs:
                m.Add(c == sum(vs))
            else:
                m.Add(c == 0)
            counts.append(c)
        if len(counts) < 2:
            return None
        hi = m.NewIntVar(0, len(p.days), f"{name}_hi")
        lo = m.NewIntVar(0, len(p.days), f"{name}_lo")
        m.AddMaxEquality(hi, counts)
        m.AddMinEquality(lo, counts)
        return hi - lo

    fair = []
    for name, w, pick in (("night", W_FAIR_NIGHT, lambda d, s: is_night(p.shifts[s])),
                          ("weekend", W_FAIR_WEEKEND, lambda d, s: iso_day(d) >= 6),
                          ("load", W_FAIR_LOAD, lambda d, s: True)):
        sp = spread(name, pick)
        if sp is not None:
            fair.append(w * sp)

    m.Minimize(W_UNCOVERED * sum(under) + sum(pref_terms) + sum(fair))
    solver = cp_model.CpSolver()
    solver.parameters.max_time_in_seconds = float(min(time_limit, MAX_TIME_LIMIT))
    solver.parameters.num_search_workers = 4
    solver.parameters.random_seed = 7
    status = solver.Solve(m)
    if status not in (cp_model.OPTIMAL, cp_model.FEASIBLE):
        return None, solver.StatusName(status)
    out = [{"employee": eid, "date": d.isoformat(), "shift": s}
           for (eid, d, s), v in x.items() if solver.BooleanValue(v)]
    out.sort(key=lambda a: (a["date"], a["shift"], a["employee"]))
    return out, solver.StatusName(status)


# ------------------------------------------------------------------ özet

def fairness(p: Problem, assignments: list[dict]) -> dict:
    nights: dict[str, int] = {e.id: 0 for e in p.emps}
    weekends: dict[str, int] = {e.id: 0 for e in p.emps}
    load: dict[str, int] = {e.id: 0 for e in p.emps}
    pref = 0
    emp_map = {e.id: e for e in p.emps}
    for a in assignments:
        d = date.fromisoformat(a["date"])
        load[a["employee"]] += 1
        if is_night(p.shifts[a["shift"]]):
            nights[a["employee"]] += 1
        if iso_day(d) >= 6:
            weekends[a["employee"]] += 1
        if p.pref_cost(emp_map[a["employee"]], d, a["shift"]):
            pref += 1

    def rng(v: dict[str, int]) -> int:
        return (max(v.values()) - min(v.values())) if v else 0

    return {"night_spread": rng(nights), "weekend_spread": rng(weekends), "load_spread": rng(load),
            "preference_conflicts": pref}


def optimize(req: OptimizeRequest) -> dict:
    p = Problem(req)
    t0 = time.monotonic()
    result, status, used = None, "", "greedy"
    if req.solver in ("auto", "cp-sat"):
        result, status = solve_cpsat(p, req.time_limit_seconds)
        if result is not None:
            used = "cp-sat"
    if result is None:
        fallback = status or ""
        result = solve_greedy(p)
        status = "GREEDY" + (f" ({fallback})" if fallback else "")
    return {
        "solver": used,
        "status": status,
        "ortools": HAS_ORTOOLS,
        "seconds": round(time.monotonic() - t0, 3),
        "assignments": result,
        "uncovered": uncovered(p, result),
        "unusable_shifts": [{"shift": s, "reason": r} for s, r in sorted(p.unusable.items())],
        "fairness": fairness(p, result),
        "violations": violations(p, result),
    }


@router.post("/optimize")
def optimize_endpoint(req: OptimizeRequest):
    if len({e.id for e in req.employees}) != len(req.employees):
        raise HTTPException(status_code=422, detail="Çalışan takma adları tekil olmalı")
    if len({s.id for s in req.shifts}) != len(req.shifts):
        raise HTTPException(status_code=422, detail="Vardiya kimlikleri tekil olmalı")
    if (max(req.days) - min(req.days)).days > MAX_DAYS:
        raise HTTPException(status_code=422, detail=f"En çok {MAX_DAYS} günlük plan önerilir")
    return optimize(req)
