import { existsSync, readFileSync } from 'node:fs'
import { fileURLToPath, URL } from 'node:url'
import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// Dev sırasında /auth, /api/* ve /ml/* istekleri çalışan bir HR360 kurulumunun
// gateway'ine proxy'lenir (varsayılan: aynı makinedeki install.sh kurulumu).
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const target = env.VITE_DEV_PROXY_TARGET || 'http://localhost'
  const proxy = { target, changeOrigin: true, secure: target.startsWith('https://') }

  // Uygulama sürümü: Docker derlemesinde HR360_VERSION build argümanı (bağlam yalnızca apps/web;
  // docker-compose.yml onu kökteki VERSION dosyasından geçirir), yerelde doğrudan ../../VERSION.
  const versionFile = fileURLToPath(new URL('../../VERSION', import.meta.url))
  const appVersion =
    env.HR360_VERSION?.trim() || (existsSync(versionFile) ? readFileSync(versionFile, 'utf8').trim() : '') || 'dev'

  return {
    plugins: [react(), tailwindcss()],
    define: {
      __APP_VERSION__: JSON.stringify(appVersion),
    },
    resolve: {
      alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
      // Tek React örneği: aksi halde motion kendi kopyasını çözer.
      dedupe: ['react', 'react-dom'],
    },
    server: {
      port: 5173,
      proxy: {
        '/api': { ...proxy },
        '/ml': { ...proxy },
        '/auth': { ...proxy },
        '/gateway': { ...proxy },
      },
    },
    build: {
      outDir: 'dist',
      sourcemap: false,
      // Vite 8 (Rolldown): `manualChunks` nesne biçimi kaldırıldı; aynı ayrım `codeSplitting.groups` ile.
      // Grup, eşleşen paketin bağımlılıklarını da alır (eski manualChunks davranışı).
      rolldownOptions: {
        output: {
          codeSplitting: {
            groups: [
              { name: 'vendor', test: /[\\/]node_modules[\\/](react|react-dom|react-router|react-router-dom|scheduler)[\\/]/, priority: 40 },
              { name: 'auth', test: /[\\/]node_modules[\\/]keycloak-js[\\/]/, priority: 30 },
              { name: 'motion', test: /[\\/]node_modules[\\/](motion|framer-motion|motion-dom|motion-utils)[\\/]/, priority: 20 },
              { name: 'query', test: /[\\/]node_modules[\\/]@tanstack[\\/](react-query|query-core)[\\/]/, priority: 10 },
            ],
          },
        },
      },
    },
  }
})
