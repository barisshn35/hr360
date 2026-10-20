import type { ReactNode } from 'react'
import { MotionConfig } from 'motion/react'
import { useReduceMotionSetting } from '@/lib/motion'

/** "Animasyonları azalt" açıkken motion/react bileşenleri sistem ayarından bağımsız olarak hareketsiz; kapalıyken sistem ayarına uyar. */
export function MotionPreference({ children }: { children: ReactNode }) {
  const [reduce] = useReduceMotionSetting()
  return <MotionConfig reducedMotion={reduce ? 'always' : 'user'}>{children}</MotionConfig>
}
