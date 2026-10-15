import type { Payslip } from '@/api/payroll'
import { formatMoney } from '@/lib/format'
import { tx, appLocale } from '@/lib/i18n'
import { requestTrace, WATERMARK_CSS, watermarkHtml } from '@/lib/watermark'

const esc = (s: string) => s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!)

/**
 * Bordro pusulasını yazdırma penceresinde açar ("PDF olarak kaydet" ile PDF alınır). Pencere tıklamayla
 * hemen açılır (açılır pencere engeline takılmasın); iz kodu gelince içerik filigranla yazılır.
 */
export function printPayslip(s: Payslip, name: string, company?: string) {
  const w = window.open('', '_blank')
  if (!w) throw new Error(tx('Açılır pencere engellendi; tarayıcıda izin verin.'))
  w.document.write(`<!doctype html><meta charset="utf-8"><title>${esc(tx('Hazırlanıyor…'))}</title><p style="font-family:Arial,sans-serif">${esc(tx('Hazırlanıyor…'))}</p>`)
  void requestTrace('payslip', { subject: s.id, format: 'print' }).then((trace) => writePayslip(w, s, name, company, trace?.text))
}

function writePayslip(w: Window, s: Payslip, name: string, company: string | undefined, watermark?: string) {
  w.document.open()
  const m = (v: number) => esc(formatMoney(v, s.currency))
  const period = new Date(s.year, s.month - 1, 1).toLocaleDateString(appLocale, { month: 'long', year: 'numeric' })
  const row = (k: string, v: string, strong = false) => `<tr${strong ? ' class="b"' : ''}><td>${esc(k)}</td><td class="r">${v}</td></tr>`
  const earnings = [
    row(tx('Aylık brüt ücret'), m(s.monthlyBaseGross)),
    row(tx('Ödenen gün / eksik gün'), `${s.paidDays} / ${s.unpaidDays}`),
    row(tx('Dönem ücreti'), m(s.baseGross)),
    s.overtimePay ? row(tx('Fazla mesai ({0} saat)', [s.overtimeHours]), m(s.overtimePay)) : '',
    s.additions ? row(tx('Ek ödemeler'), m(s.additions)) : '',
    row(tx('Toplam brüt'), m(s.gross), true),
  ].join('')
  const deductions = [
    row(tx('SGK işçi payı'), m(s.sgkEmployee)),
    row(tx('İşsizlik sigortası işçi payı'), m(s.unemploymentEmployee)),
    row(tx('Gelir vergisi (istisna sonrası)'), m(s.incomeTax - s.incomeTaxExemption)),
    row(tx('Damga vergisi (istisna sonrası)'), m(s.stampTax - s.stampTaxExemption)),
    s.deductions ? row(tx('Diğer kesintiler'), m(s.deductions)) : '',
    row(tx('Toplam kesinti'), m(s.gross - s.net), true),
  ].join('')
  const info = [
    row(tx('SGK matrahı'), m(s.sgkBase)),
    row(tx('Gelir vergisi matrahı'), m(s.taxBase)),
    row(tx('Kümülatif matrah'), m(s.cumulativeTaxBase)),
    row(tx('Asgari ücret GV istisnası'), m(s.incomeTaxExemption)),
    row(tx('Asgari ücret damga istisnası'), m(s.stampTaxExemption)),
  ].join('')
  w.document.write(`<!doctype html><html lang="${appLocale}"><head><meta charset="utf-8"><title>${esc(tx('Bordro pusulası'))} — ${esc(name)} — ${esc(period)}</title>
<style>@page{size:A4;margin:18mm}body{font-family:Inter,'Segoe UI',Arial,sans-serif;color:#111;font-size:11pt}
h1{font-size:16pt;margin:0 0 2mm}p.s{margin:0 0 6mm;color:#555}table{width:100%;border-collapse:collapse;margin-bottom:5mm}
th{text-align:left;font-size:10pt;color:#555;border-bottom:1px solid #ccc;padding:2mm 0}td{padding:1.4mm 0;border-bottom:1px solid #eee}
td.r{text-align:right;font-variant-numeric:tabular-nums}tr.b td{font-weight:600}.net{font-size:14pt;font-weight:700;text-align:right;margin-top:4mm}
.g{display:grid;grid-template-columns:1fr 1fr;gap:8mm}footer{margin-top:10mm;font-size:8.5pt;color:#777}${WATERMARK_CSS}</style></head><body>${watermarkHtml(watermark)}
<h1>${esc(tx('Bordro pusulası'))}</h1><p class="s">${esc(name)} · ${esc(period)}${company ? ' · ' + esc(company) : ''}</p>
<div class="g"><table><tr><th colspan="2">${esc(tx('Kazançlar'))}</th></tr>${earnings}</table>
<table><tr><th colspan="2">${esc(tx('Kesintiler'))}</th></tr>${deductions}</table></div>
<table><tr><th colspan="2">${esc(tx('Matrah bilgileri'))}</th></tr>${info}</table>
<div class="net">${esc(tx('Net ödenecek'))}: ${m(s.net)}</div>
<footer>${esc(tx('Bu belge kişisel veri içerir; yalnızca çalışan ve bordro yetkilisi içindir (KVKK).'))}</footer>
<script>window.onload=()=>setTimeout(()=>window.print(),300)<\/script></body></html>`)
  w.document.close()
}
