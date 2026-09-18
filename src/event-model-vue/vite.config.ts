import { fileURLToPath, URL } from 'node:url'
// vitest/config re-exports vite's defineConfig with the `test` block typed.
import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'
import dts from 'vite-plugin-dts'

export default defineConfig({
  plugins: [vue(), dts({ rollupTypes: true, tsconfigPath: './tsconfig.json' })],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) }
  },
  build: {
    lib: {
      entry: fileURLToPath(new URL('./src/index.ts', import.meta.url)),
      name: 'EventModelVue',
      fileName: 'event-model-vue',
      formats: ['es']
    },
    // The peer stays external: a consumer supplies its own Vue, and bundling a second copy
    // of Vue is how you get two reactivity systems that cannot see each other. (@vue-flow/core
    // left the peer list in 5679e2d — nothing here imports it any more.)
    rollupOptions: { external: ['vue'] }
  },
  test: {
    environment: 'happy-dom',
    globals: true
  }
})
