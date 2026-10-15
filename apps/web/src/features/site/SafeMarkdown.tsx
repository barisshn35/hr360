/**
 * Yasal metinler ve SSS cevapları için küçük, güvenli Markdown işleyici.
 *
 * Metin platform yöneticisinden gelir ama yine de HTML olarak YORUMLANMAZ: her öğe React
 * elemanı olarak üretilir (innerHTML yok), bağlantılar yalnızca http(s), mailto, tel ve site içi
 * adreslere izinlidir. Desteklenen alt küme: başlıklar (#–####), paragraflar, madde ve numaralı
 * listeler, alıntı, yatay çizgi, basit tablolar, **kalın**, *italik*, `kod`, [bağlantı](adres).
 */
import { Fragment, type ReactNode } from 'react'
import { cn } from '@/lib/utils'

type Block =
  | { kind: 'h'; level: number; text: string }
  | { kind: 'p'; text: string }
  | { kind: 'ul' | 'ol'; items: string[] }
  | { kind: 'quote'; text: string }
  | { kind: 'hr' }
  | { kind: 'table'; head: string[]; rows: string[][] }

const SAFE_HREF = /^(https?:\/\/|mailto:|tel:|\/(?!\/)|#)/i

/** Başlık metninden çapa kimliği (Türkçe harfler sadeleştirilir): "Çerezler" → "cerezler". */
export function slugify(text: string): string {
  const map: Record<string, string> = { ç: 'c', ğ: 'g', ı: 'i', i: 'i', ö: 'o', ş: 's', ü: 'u', â: 'a', î: 'i', û: 'u' }
  return text
    .toLocaleLowerCase('tr')
    .replace(/[çğıöşüâîû]/g, (c) => map[c] ?? c)
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 80)
}

function cells(line: string): string[] {
  return line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((c) => c.trim())
}

export function parseBlocks(src: string): Block[] {
  const lines = src.replace(/\r\n?/g, '\n').split('\n')
  const blocks: Block[] = []
  let i = 0
  while (i < lines.length) {
    const line = lines[i]
    const t = line.trim()
    if (!t) { i++; continue }
    const h = /^(#{1,4})\s+(.*)$/.exec(t)
    if (h) { blocks.push({ kind: 'h', level: h[1].length, text: h[2].trim() }); i++; continue }
    if (/^(-{3,}|\*{3,}|_{3,})$/.test(t)) { blocks.push({ kind: 'hr' }); i++; continue }
    if (t.startsWith('|') && i + 1 < lines.length && /^\|?\s*:?-{2,}/.test(lines[i + 1].trim())) {
      const head = cells(t)
      const rows: string[][] = []
      i += 2
      while (i < lines.length && lines[i].trim().startsWith('|')) { rows.push(cells(lines[i])); i++ }
      blocks.push({ kind: 'table', head, rows })
      continue
    }
    if (/^[-*•]\s+/.test(t) || /^\d+[.)]\s+/.test(t)) {
      const ordered = /^\d+[.)]\s+/.test(t)
      const re = ordered ? /^\d+[.)]\s+/ : /^[-*•]\s+/
      const items: string[] = []
      while (i < lines.length && re.test(lines[i].trim())) {
        let item = lines[i].trim().replace(re, '')
        i++
        // Girintili devam satırları aynı maddeye eklenir.
        while (i < lines.length && /^\s{2,}\S/.test(lines[i]) && !re.test(lines[i].trim())) { item += ' ' + lines[i].trim(); i++ }
        items.push(item)
      }
      blocks.push({ kind: ordered ? 'ol' : 'ul', items })
      continue
    }
    if (t.startsWith('>')) {
      const parts: string[] = []
      while (i < lines.length && lines[i].trim().startsWith('>')) { parts.push(lines[i].trim().replace(/^>\s?/, '')); i++ }
      blocks.push({ kind: 'quote', text: parts.join(' ') })
      continue
    }
    const parts: string[] = []
    while (i < lines.length && lines[i].trim() && !/^(#{1,4}\s|[-*•]\s|\d+[.)]\s|>|\|)/.test(lines[i].trim()) && !/^(-{3,}|\*{3,}|_{3,})$/.test(lines[i].trim())) {
      parts.push(lines[i].trim())
      i++
    }
    blocks.push({ kind: 'p', text: parts.join(' ') })
  }
  return blocks
}

/** Satır içi biçimler: `kod`, **kalın**, *italik* / _italik_, [metin](adres). */
export function inline(text: string, keyPrefix = 'i'): ReactNode[] {
  const out: ReactNode[] = []
  const re = /(`[^`]+`)|(\*\*[^*]+\*\*)|(\*[^*\s][^*]*\*|_[^_\s][^_]*_)|(\[[^\]]+\]\([^)\s]+\))/g
  let last = 0
  let m: RegExpExecArray | null
  let n = 0
  while ((m = re.exec(text))) {
    if (m.index > last) out.push(text.slice(last, m.index))
    const key = `${keyPrefix}-${n++}`
    const tok = m[0]
    if (m[1]) out.push(<code key={key} className="rounded bg-muted px-1.5 py-0.5 font-mono text-[0.9em]">{tok.slice(1, -1)}</code>)
    else if (m[2]) out.push(<strong key={key} className="font-semibold text-foreground">{inline(tok.slice(2, -2), key)}</strong>)
    else if (m[3]) out.push(<em key={key}>{inline(tok.slice(1, -1), key)}</em>)
    else {
      const lm = /^\[([^\]]+)\]\(([^)\s]+)\)$/.exec(tok)!
      const href = lm[2]
      if (SAFE_HREF.test(href)) {
        const external = /^https?:\/\//i.test(href)
        out.push(
          <a
            key={key}
            href={href}
            className="font-medium text-primary underline-offset-4 hover:underline"
            {...(external ? { target: '_blank', rel: 'noopener noreferrer' } : {})}
          >
            {lm[1]}
          </a>,
        )
      } else {
        out.push(lm[1])
      }
    }
    last = m.index + tok.length
  }
  if (last < text.length) out.push(text.slice(last))
  return out
}

export function SafeMarkdown({ source, className }: { source: string; className?: string }) {
  const blocks = parseBlocks(source)
  return (
    <div className={cn('space-y-4 text-[15px] leading-[1.75] text-muted-foreground', className)}>
      {blocks.map((b, i) => {
        const k = `b${i}`
        switch (b.kind) {
          case 'h': {
            const id = slugify(b.text)
            if (b.level === 1) return <h2 key={k} id={id} className="scroll-mt-24 pt-2 text-[24px] font-semibold tracking-tight text-foreground text-balance">{inline(b.text, k)}</h2>
            if (b.level === 2) return <h2 key={k} id={id} className="scroll-mt-24 pt-4 text-[19px] font-semibold tracking-tight text-foreground text-balance">{inline(b.text, k)}</h2>
            return <h3 key={k} id={id} className="scroll-mt-24 pt-2 text-[16px] font-semibold text-foreground">{inline(b.text, k)}</h3>
          }
          case 'p':
            return <p key={k}>{inline(b.text, k)}</p>
          case 'ul':
            return (
              <ul key={k} className="list-disc space-y-1.5 pl-5 marker:text-primary">
                {b.items.map((it, j) => <li key={j}>{inline(it, `${k}-${j}`)}</li>)}
              </ul>
            )
          case 'ol':
            return (
              <ol key={k} className="list-decimal space-y-1.5 pl-5 marker:text-primary marker:font-medium">
                {b.items.map((it, j) => <li key={j}>{inline(it, `${k}-${j}`)}</li>)}
              </ol>
            )
          case 'quote':
            return <blockquote key={k} className="border-l-2 border-primary/50 pl-4 italic">{inline(b.text, k)}</blockquote>
          case 'hr':
            return <hr key={k} className="border-border" />
          case 'table':
            return (
              <div key={k} className="overflow-x-auto rounded-xl border border-border">
                <table className="w-full min-w-[520px] text-left text-[13.5px]">
                  <thead className="bg-muted/50 text-foreground">
                    <tr>{b.head.map((c, j) => <th key={j} className="px-3.5 py-2.5 font-medium">{inline(c, `${k}-h${j}`)}</th>)}</tr>
                  </thead>
                  <tbody>
                    {b.rows.map((r, j) => (
                      <tr key={j} className="border-t border-border align-top">
                        {r.map((c, x) => <td key={x} className="px-3.5 py-2.5">{inline(c, `${k}-${j}-${x}`)}</td>)}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )
          default:
            return <Fragment key={k} />
        }
      })}
    </div>
  )
}
