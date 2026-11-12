import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  base: './',
  build: {
    outDir: 'dist',
    sourcemap: false,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (!id.includes('node_modules')) {
            if (id.includes('/locales/')) return 'locales';
            return;
          }
          if (id.includes('@xyflow')) return 'flow';
          if (id.includes('@dnd-kit')) return 'dnd';
          if (id.includes('lucide-react')) return 'icons';
        },
      },
    },
  },
});
