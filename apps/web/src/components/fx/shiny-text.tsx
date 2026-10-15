/**
 * Üzerinden ışık geçen metin.
 * Kaynak: 21st.dev "Animated Shiny Text" (anurag-mishra22, id 215) ve
 * "Shiny Text" (shadcnspace, id 19109). Uyarlama: saf CSS (animate-shine),
 * renkler token'lardan.
 */
import { cn } from '@/lib/utils'

export function ShinyText({ children, className }: { children: React.ReactNode; className?: string }) {
  return (
    <span
      className={cn(
        'animate-shine bg-[length:250%_100%] bg-clip-text text-transparent',
        'bg-[linear-gradient(110deg,hsl(var(--primary))_35%,hsl(160_90%_85%)_50%,hsl(var(--primary))_65%)]',
        className,
      )}
    >
      {children}
    </span>
  )
}

/** Yavaşça akan çok duraklı zümrüt geçişli başlık metni. */
export function GradientText({ children, className }: { children: React.ReactNode; className?: string }) {
  return (
    <span
      className={cn(
        'animate-gradient-x bg-[length:200%_200%] bg-clip-text text-transparent',
        'bg-[linear-gradient(90deg,hsl(var(--foreground))_0%,hsl(var(--foreground))_35%,hsl(var(--primary))_55%,hsl(var(--foreground))_75%,hsl(var(--foreground))_100%)]',
        className,
      )}
    >
      {children}
    </span>
  )
}
