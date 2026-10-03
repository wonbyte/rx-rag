import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// In dev, Vite forwards /api calls to the .NET API.
// The browser sees one origin, so we need no CORS rules.
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: { '/api': 'http://localhost:5080' },
  },
})
