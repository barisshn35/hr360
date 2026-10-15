// HR360 yük testi (k6). Gerçekçi bir iş günü karışımı:
//   %70 çalışan (ana sayfa, izin, bildirim, takdir), %25 yönetici (onaylar, ekip), %5 İK (analitik, denetim).
// Çalıştırma: scripts/loadtest.sh [profil]   (profil: smoke | load | stress; varsayılan load)
// Ortam: BASE_URL (ör. http://gateway), TOKENS (JSON: {"ayse": "...", "mehmet": "...", "admin": "..."}), PROFILE.
// Erişim jetonları 5 dk geçerlidir; profiller bu süreyi aşmaz.

import http from 'k6/http'
import { check, group, sleep } from 'k6'
import { Rate, Trend } from 'k6/metrics'

const BASE = __ENV.BASE_URL || 'http://localhost'
const TOKENS = JSON.parse(__ENV.TOKENS || '{}')
const PROFILE = __ENV.PROFILE || 'load'

const profiles = {
  smoke: { stages: [{ duration: '10s', target: 5 }, { duration: '30s', target: 5 }] },
  load: { stages: [{ duration: '30s', target: 50 }, { duration: '2m', target: 100 }, { duration: '30s', target: 0 }] },
  stress: { stages: [{ duration: '30s', target: 100 }, { duration: '1m', target: 250 }, { duration: '1m', target: 400 }, { duration: '30s', target: 0 }] },
}

export const options = {
  scenarios: {
    employees: { executor: 'ramping-vus', exec: 'employee', startVUs: 0, stages: scale(profiles[PROFILE].stages, 0.7) },
    managers: { executor: 'ramping-vus', exec: 'manager', startVUs: 0, stages: scale(profiles[PROFILE].stages, 0.25) },
    hr: { executor: 'ramping-vus', exec: 'hr', startVUs: 0, stages: scale(profiles[PROFILE].stages, 0.05) },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{kind:read}': ['p(95)<800', 'p(99)<2000'],
    'http_req_duration{kind:report}': ['p(95)<3000'],
    checks: ['rate>0.99'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
}

// Uç bazında p95 raporu için her ucun alt metriği eşik olarak tanımlanır (k6 yalnızca
// eşiği olan etiket kombinasyonlarını özette gösterir).
const ENDPOINTS = [
  '/api/employee/employees/me', '/api/notification/notifications', '/api/governance/plan', '/api/engagement/celebrations',
  '/api/leave/leave-balances', '/api/leave/leave-requests', '/api/engagement/kudos', '/api/engagement/workplace/presence',
  '/api/governance/meetings', '/api/workflow/workflows', '/api/engagement/team-health', '/api/engagement/one-on-ones',
  '/api/organization/departments', '/api/employee/employees', '/api/governance/analytics/overview', '/api/governance/audit',
  '/api/governance/events/recent',
]
for (const e of ENDPOINTS) options.thresholds[`http_req_duration{name:${e}}`] = ['p(95)<5000']

function scale(stages, share) {
  return stages.map((s) => ({ duration: s.duration, target: Math.max(s.target === 0 ? 0 : 1, Math.round(s.target * share)) }))
}

const errors = new Rate('hr360_errors')
const pageTime = new Trend('hr360_page_time', true)

function get(who, path, kind = 'read') {
  const res = http.get(`${BASE}${path}`, {
    headers: { Authorization: `Bearer ${TOKENS[who]}`, Accept: 'application/json' },
    tags: { kind, name: path.split('?')[0] },
  })
  const ok = check(res, { [`${path.split('?')[0]} 200`]: (r) => r.status === 200 })
  errors.add(!ok)
  return res
}

/** Bir "ekran": tarayıcının paralel attığı istekler tek batch olarak. */
function screen(who, paths, kind = 'read') {
  const started = Date.now()
  const reqs = paths.map((p) => ['GET', `${BASE}${p}`, null, {
    headers: { Authorization: `Bearer ${TOKENS[who]}`, Accept: 'application/json' }, tags: { kind, name: p.split('?')[0] },
  }])
  const res = http.batch(reqs)
  res.forEach((r, i) => errors.add(!check(r, { [`${paths[i].split('?')[0]} 200`]: (x) => x.status === 200 })))
  pageTime.add(Date.now() - started)
}

export function employee() {
  group('ana sayfa', () => screen('ayse', ['/api/employee/employees/me?optional=true', '/api/notification/notifications', '/api/governance/plan', '/api/engagement/celebrations']))
  sleep(1 + Math.random() * 2)
  group('izin', () => screen('ayse', ['/api/leave/leave-balances', '/api/leave/leave-requests']))
  sleep(1 + Math.random() * 2)
  group('takdir ve ofis', () => screen('ayse', ['/api/engagement/kudos', '/api/engagement/workplace/presence', '/api/governance/meetings']))
  sleep(2 + Math.random() * 3)
}

export function manager() {
  group('onay kutusu', () => screen('mehmet', ['/api/workflow/workflows?status=Pending', '/api/notification/notifications']))
  sleep(1 + Math.random() * 2)
  group('ekip', () => screen('mehmet', ['/api/engagement/team-health', '/api/engagement/one-on-ones', '/api/organization/departments', '/api/employee/employees']))
  sleep(2 + Math.random() * 3)
}

export function hr() {
  group('analitik', () => get('admin', '/api/governance/analytics/overview?months=12', 'report'))
  sleep(2 + Math.random() * 2)
  group('denetim', () => screen('admin', ['/api/governance/audit?take=50', '/api/governance/events/recent']))
  sleep(3 + Math.random() * 3)
}

export function handleSummary(data) {
  const m = data.metrics
  const v = (name, stat) => (m[name] && m[name].values[stat] !== undefined ? m[name].values[stat] : null)
  const rows = Object.entries(m)
    .filter(([k]) => k.startsWith('http_req_duration{name:'))
    .map(([k, x]) => ({ endpoint: k.slice('http_req_duration{name:'.length, -1), p95: x.values['p(95)'], avg: x.values.avg }))
  const summary = {
    profile: PROFILE,
    at: new Date().toISOString(),
    requests: v('http_reqs', 'count'),
    rps: v('http_reqs', 'rate'),
    failedRate: v('http_req_failed', 'rate'),
    p95: v('http_req_duration', 'p(95)'),
    p99: v('http_req_duration', 'p(99)'),
    avg: v('http_req_duration', 'avg'),
    screenP95: v('hr360_page_time', 'p(95)'),
    maxVUs: v('vus_max', 'max'),
    thresholdsPassed: Object.values(data.metrics).every((x) => !x.thresholds || Object.values(x.thresholds).every((t) => t.ok)),
  }
  return {
    stdout: `\nHR360 yük testi (${PROFILE}): ${summary.requests} istek, ${summary.rps.toFixed(1)} istek/sn, ` +
      `hata %${(summary.failedRate * 100).toFixed(2)}, p95 ${summary.p95.toFixed(0)} ms, p99 ${summary.p99.toFixed(0)} ms, ` +
      `ekran p95 ${summary.screenP95.toFixed(0)} ms, eşikler ${summary.thresholdsPassed ? 'GEÇTİ' : 'KALDI'}\n`,
    '/results/summary.json': JSON.stringify({ summary, endpoints: rows }, null, 2),
  }
}
