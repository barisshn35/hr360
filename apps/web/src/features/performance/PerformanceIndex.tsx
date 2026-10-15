import { Navigate } from 'react-router-dom'
import { useAuth } from '@/auth/useAuth'

/**
 * `/panel/performans` — role göre doğru başlangıç ekranına yönlendirir.
 * Çalışan kendi görünümüne, yönetici kurulum ve takip ekranlarına gider.
 */
export function PerformanceIndex() {
  const { can } = useAuth()
  return <Navigate to={can('performance:manage') ? '/panel/performans/analiz' : '/panel/performans/benim'} replace />
}
