import { cp, rm, stat } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const hostRoot = resolve(webRoot, '../src/Router.Host')
const dist = resolve(webRoot, 'dist')
const wwwroot = resolve(hostRoot, 'wwwroot')

// Docker builds the frontend in a separate stage, without the host project.
if ((await stat(resolve(hostRoot, 'Router.Host.csproj')).catch(() => null))?.isFile()) {
  if (!(await stat(resolve(dist, 'index.html')).catch(() => null))?.isFile()) {
    throw new Error('Frontend build output is missing: web/dist/index.html')
  }
  await rm(wwwroot, { recursive: true, force: true })
  await cp(dist, wwwroot, { recursive: true })
  console.log(`Frontend assets copied to ${wwwroot}`)
}
