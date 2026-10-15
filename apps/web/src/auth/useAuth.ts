import { useContext } from 'react'
import { AuthContext } from './AuthContext'
import { tx } from '@/lib/i18n'

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error(tx('useAuth yalnızca <AuthProvider> içinde kullanılabilir.'))
  return ctx
}
