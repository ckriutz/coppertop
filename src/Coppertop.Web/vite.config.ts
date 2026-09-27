import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'

// In dev, /api is proxied to the Coppertop Api. In Docker, nginx does the same job.
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const target = env.COPPERTOP_API_URL ?? 'http://localhost:5057'
  return {
    plugins: [react()],
    server: {
      proxy: {
        '/api': {
          target,
          changeOrigin: true,
          rewrite: (path) => path.replace(/^\/api/, ''),
          headers: env.COPPERTOP_API_KEY ? { 'X-Api-Key': env.COPPERTOP_API_KEY } : {},
        },
      },
    },
  }
})
