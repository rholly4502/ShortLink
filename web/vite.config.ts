import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// https://vite.dev/config/
export default defineConfig({
  base: '/shortlink/',
  plugins: [react()],
  server: {
    proxy: {
      // Forward API calls to the backend during dev; strip the subpath prefix
      // so ASP.NET Core sees its own controller routes (/auth/*, /api/links).
      '/shortlink/api': {
        target: 'http://localhost:8080',
        changeOrigin: true,
        rewrite: (p) => p.replace(/^\/shortlink\/api/, ''),
      },
    },
  },
  test: {
    environment: 'node',
  },
})
