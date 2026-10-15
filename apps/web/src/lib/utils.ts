import { clsx, type ClassValue } from 'clsx'
import { extendTailwindMerge } from 'tailwind-merge'

/**
 * tailwind-merge 3 (Tailwind v4) yalnızca varsayılan tema ölçeklerini tanır. `src/styles/index.css`
 * `@theme` içindeki özel gölge/animasyon adları burada bildirilmezse `shadow-card` bir gölge
 * RENGİ sanılır (`shadow-card shadow-black/5` → `shadow-card` düşer) ve `animate-*` çakışmaları
 * birleşmez. `@theme`'e yeni `--shadow-*` / `--animate-*` eklenirse bu listeye de ekleyin.
 */
const twMerge = extendTailwindMerge({
  extend: {
    theme: {
      shadow: ['card', 'pop', 'popover', 'surface'],
      animate: [
        'border-beam', 'meteor', 'orbit', 'shimmer-slide', 'spin-around',
        'blob', 'shine', 'float', 'gradient-x',
      ],
    },
  },
})

/**
 * shadcn/21st.dev bileşenlerinin beklediği className birleştirici.
 * tailwind-merge sayesinde `p-2` + `p-4` gibi çakışmalarda sonuncusu kazanır —
 * bileşenlere dışarıdan className geçirip üzerine yazabilmek için gerekli.
 */
export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}
