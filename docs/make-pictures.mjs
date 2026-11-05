// Draws the README's pictures from docs/showcase.html:
//   node docs/make-pictures.mjs          every picture
//   node docs/make-pictures.mjs hero     just one
// It needs Node 24, Chrome or Edge, and ffmpeg. Set CHROME if neither browser is in the usual place.
// The social card also has to be uploaded by hand: Settings, General, Social preview.
import { execFileSync, spawn } from 'node:child_process'
import { existsSync, mkdtempSync, mkdirSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const DOCS = resolve(fileURLToPath(import.meta.url), '..')
const IMAGES = join(DOCS, 'images')
const TEMP = mkdtempSync(join(tmpdir(), 'make-pictures-'))
const PICTURES = {
  hero: { file: 'hero.gif', width: 1280, height: 600, scale: 1.25, seconds: 8.8, fps: 20 },
  compare: { file: 'before-after.jpg', width: 1000, height: 640, scale: 2 },
  steps: { file: 'steps.jpg', width: 1000, height: 640, scale: 2 },
  colors: { file: 'colors.jpg', width: 1000, height: 560, scale: 2 },
  locked: { file: 'locked.gif', width: 1000, height: 520, scale: 1.25, seconds: 5.2, fps: 20 },
  social: { file: 'social-preview.png', width: 1280, height: 640, scale: 1 },
}
const browser = process.env.CHROME ?? ['C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find(existsSync)

const chrome = spawn(browser, ['--headless=new', '--hide-scrollbars', '--force-color-profile=srgb', '--remote-debugging-port=0', `--user-data-dir=${join(TEMP, 'profile')}`, 'about:blank'], { stdio: ['ignore', 'ignore', 'pipe'] })
const address = await new Promise(done => {
  let text = ''
  chrome.stderr.on('data', part => {
    text += part
    const found = text.match(/ws:\/\/\S+/)
    if (found) done(found[0])
  })
})
const socket = new WebSocket(address)
await new Promise(done => { socket.onopen = done })
const waiting = new Map()
let nextId = 1
socket.onmessage = event => {
  const reply = JSON.parse(event.data)
  if (!waiting.has(reply.id)) return
  const { done, fail } = waiting.get(reply.id)
  waiting.delete(reply.id)
  if (reply.error) fail(new Error(reply.error.message))
  else done(reply.result)
}
const send = (method, params = {}, sessionId) => new Promise((done, fail) => {
  const id = nextId++
  waiting.set(id, { done, fail })
  socket.send(JSON.stringify({ id, method, params, sessionId }))
})

const { targetId } = await send('Target.createTarget', { url: 'about:blank' })
const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true })
const page = (method, params) => send(method, params, sessionId)
const evaluate = async expression => {
  const { result, exceptionDetails } = await page('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })
  if (exceptionDetails) throw new Error(exceptionDetails.exception?.description ?? exceptionDetails.text)
  return result.value
}

const report = path => console.log(`${String(statSync(path).size).padStart(9)}  docs/images/${path.split(/[\\/]/).pop()}`)
mkdirSync(IMAGES, { recursive: true })

async function draw(name, { file, width, height, scale, seconds, fps }) {
  await page('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: scale, mobile: false })
  await page('Page.enable')
  await page('Page.navigate', { url: `${pathToFileURL(join(DOCS, 'showcase.html')).href}?scene=${name}` })
  for (let tries = 0; !(await evaluate('typeof window.ready === "object"')); tries++) {
    if (tries > 200) throw new Error(`${name} never started`)
    await new Promise(done => setTimeout(done, 50))
  }
  await evaluate('window.ready')
  const out = join(IMAGES, file)
  if (!seconds) {
    const format = file.endsWith('.jpg') ? { format: 'jpeg', quality: 92 } : { format: 'png' }
    const { data } = await page('Page.captureScreenshot', format)
    writeFileSync(out, Buffer.from(data, 'base64'))
    return report(out)
  }
  const frames = join(TEMP, name)
  mkdirSync(frames)
  const count = Math.round(seconds * fps)
  for (let i = 0; i < count; i++) {
    await evaluate(`seek(${i / fps})`)
    const { data } = await page('Page.captureScreenshot', { format: 'png' })
    writeFileSync(join(frames, `${String(i).padStart(4, '0')}.png`), Buffer.from(data, 'base64'))
  }
  // A fixed dither pattern keeps still parts identical between frames, which is most of what keeps a GIF small.
  execFileSync('ffmpeg', ['-y', '-loglevel', 'error', '-framerate', String(fps), '-i', join(frames, '%04d.png'), '-filter_complex', 'split[a][b];[a]palettegen=max_colors=256:stats_mode=full[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle', '-loop', '0', out])
  report(out)
}

const wanted = process.argv.slice(2)
try {
  for (const [name, picture] of Object.entries(PICTURES)) if (!wanted.length || wanted.includes(name)) await draw(name, picture)
} finally {
  socket.close()
  chrome.kill()
  await new Promise(done => setTimeout(done, 500)) // The browser lets go of its profile folder.
  rmSync(TEMP, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 })
}
