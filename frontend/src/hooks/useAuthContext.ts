import { useEffect, useState } from 'react'
import { getAuthContext } from '../api'

export function useAuthContext() {
  const [token, setToken] = useState<string | null>(() => localStorage.getItem('authToken'))
  const [context, setContext] = useState<{ requiresAuth: boolean; host: string } | null>(null)
  const [error, setError] = useState<string>('')

  useEffect(() => {
    getAuthContext()
      .then((ctx) => {
        setContext(ctx)
        if (!ctx.requiresAuth) {
          setToken('local')
        }
      })
      .catch((err) => setError(err.message))
  }, [])

  const saveToken = (value: string | null) => {
    if (value) {
      localStorage.setItem('authToken', value)
    } else {
      localStorage.removeItem('authToken')
    }
    setToken(value)
  }

  return { token, context, error, saveToken }
}

