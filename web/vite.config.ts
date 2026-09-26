import { readFile, writeFile } from 'node:fs/promises'
import { extname, resolve } from 'node:path'
import { fileURLToPath, URL } from 'node:url'
import { brotliCompress, constants, gzip } from 'node:zlib'
import { promisify } from 'node:util'
import type { Plugin } from 'vite'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

const gzipAsync = promisify(gzip)
const brotliCompressAsync = promisify(brotliCompress)
const compressibleExtensions = new Set(['.html', '.js', '.css', '.json', '.svg'])

function compressBuiltAssets(): Plugin {
  let outputDirectory = ''

  return {
    name: 'compress-built-assets',
    apply: 'build',
    configResolved(config) {
      outputDirectory = resolve(config.root, config.build.outDir)
    },
    async generateBundle(_options, bundle) {
      for (const [fileName, asset] of Object.entries(bundle)) {
        if (!compressibleExtensions.has(extname(fileName))) continue

        const source = Buffer.from(
          asset.type === 'chunk' ? asset.code : asset.source)
        const [gzipSource, brotliSource] = await Promise.all([
          gzipAsync(source, { level: 9 }),
          brotliCompressAsync(source, {
            params: {
              [constants.BROTLI_PARAM_QUALITY]: 5,
            },
          }),
        ])

        this.emitFile({
          type: 'asset',
          fileName: `${fileName}.gz`,
          source: gzipSource,
        })
        this.emitFile({
          type: 'asset',
          fileName: `${fileName}.br`,
          source: brotliSource,
        })
      }
    },
    async closeBundle() {
      const fileName = resolve(outputDirectory, 'index.html')
      const source = await readFile(fileName)
      const [gzipSource, brotliSource] = await Promise.all([
        gzipAsync(source, { level: 9 }),
        brotliCompressAsync(source, {
          params: {
            [constants.BROTLI_PARAM_QUALITY]: 5,
          },
        }),
      ])

      await Promise.all([
        writeFile(`${fileName}.gz`, gzipSource),
        writeFile(`${fileName}.br`, brotliSource),
      ])
    },
  }
}

export default defineConfig({
  plugins: [vue(), compressBuiltAssets()],
  build: {
    emptyOutDir: true,
  },
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5242',
      '/v1': 'http://localhost:5242',
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    css: true,
  },
})
