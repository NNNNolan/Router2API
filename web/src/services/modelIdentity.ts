import deepseek from '@lobehub/icons-static-svg/icons/deepseek-color.svg'
import qwen from '@lobehub/icons-static-svg/icons/qwen-color.svg'
import claude from '@lobehub/icons-static-svg/icons/claude-color.svg'
import gemini from '@lobehub/icons-static-svg/icons/gemini-color.svg'
import meta from '@lobehub/icons-static-svg/icons/meta-color.svg'
import mistral from '@lobehub/icons-static-svg/icons/mistral-color.svg'
import kimi from '@lobehub/icons-static-svg/icons/kimi-color.svg'
import zhipu from '@lobehub/icons-static-svg/icons/zhipu-color.svg'
import minimax from '@lobehub/icons-static-svg/icons/minimax-color.svg'
import grok from '@lobehub/icons-static-svg/icons/grok.svg'
import openai from '@lobehub/icons-static-svg/icons/openai.svg'
import nvidia from '@lobehub/icons-static-svg/icons/nvidia-color.svg'

type ModelIdentity = { name: string; initials: string; icon?: string }
type Brand = ModelIdentity & { family: RegExp; org: RegExp }

const brands: Brand[] = [
  { name: 'DeepSeek', initials: 'DS', icon: deepseek, family: /\bdeep\s?seek\b/, org: /^deepseek(?:-ai)?$/ },
  { name: 'NVIDIA', initials: 'NV', icon: nvidia, family: /\bnemotron\b/, org: /^nvidia$/ },
  { name: 'Qwen', initials: 'QW', icon: qwen, family: /\b(qwen|qwq|qvq)\b/, org: /^qwen$/ },
  { name: 'Claude', initials: 'CL', icon: claude, family: /\bclaude\b/, org: /^anthropic$/ },
  { name: 'Gemini', initials: 'GM', icon: gemini, family: /\b(gemini|gemma)\b/, org: /^(google|gemini)$/ },
  { name: 'Llama', initials: 'LM', icon: meta, family: /\b(llama|muse spark)\b/, org: /^(meta|meta-llama|facebook)$/ },
  { name: 'Mistral', initials: 'MI', icon: mistral, family: /\b(mistral|mixtral|codestral|magistral|devstral|ministral|pixtral)\b/, org: /^mistralai$/ },
  { name: 'Kimi', initials: 'KM', icon: kimi, family: /\b(kimi|moonshot)\b/, org: /^moonshot(?:ai)?$/ },
  { name: 'GLM', initials: 'GL', icon: zhipu, family: /\b(glm|chatglm)\b/, org: /^(zai-org|zhipu|zhipuai|thudm)$/ },
  { name: 'MiniMax', initials: 'MM', icon: minimax, family: /\b(minimax|mini max|abab|hailuo)\b/, org: /^minimax(?:ai)?$/ },
  { name: 'Grok', initials: 'GK', icon: grok, family: /\bgrok\b/, org: /^(x-ai|xai)$/ },
  { name: 'OpenAI', initials: 'OA', icon: openai, family: /\b(gpt|chatgpt|whisper|dall e)\b|\bo [134]\b/, org: /^openai$/ },
]

function words(value: string) {
  return value.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase().replace(/([a-z])(\d)/g, '$1 $2').replace(/(\d)([a-z])/g, '$1 $2').replace(/[-_./:]+/g, ' ')
}

export function modelInitials(value: string) {
  const name = value.replace(/[-_\s]+v?\d.*$/i, '').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2').replace(/([a-z])([A-Z])/g, '$1 $2')
  const parts = name.match(/[\p{L}\p{N}]+/gu) ?? []
  if (!parts.length) return '?'
  if (/\p{Script=Han}/u.test(parts[0]!)) return Array.from(parts[0]!).slice(0, 2).join('')
  const initials = parts.length > 1 ? `${Array.from(parts[0]!)[0]}${Array.from(parts[1]!)[0]}` : Array.from(parts[0]!).slice(0, 2).join('')
  return Array.from(initials.toUpperCase()).slice(0, 2).join('')
}

export function resolveModelIdentity(id: string, displayName = '', platform = ''): ModelIdentity {
  let normalized = id.trim()
  const prefix = platform.trim().toLowerCase()
  if (prefix && normalized.toLowerCase().startsWith(`${prefix}/`)) normalized = normalized.slice(prefix.length + 1)
  const segments = normalized.split('/')
  const modelName = segments.at(-1)?.replace(/:.*$/, '') ?? ''
  const modelWords = words(modelName)
  const derivedName = modelWords.split(/\bdistill\b/)[0]!
  const family = brands.find(brand => brand.family.test(derivedName))
    ?? brands.find(brand => brand.family.test(modelWords))
    ?? brands.find(brand => segments.slice(0, -1).some(segment => brand.org.test(segment.toLowerCase())))
    ?? brands.find(brand => brand.family.test(words(displayName)))
  if (family) return { name: family.name, initials: family.initials, icon: family.icon }
  const name = displayName.trim() || modelName
  return { name, initials: modelInitials(name) }
}
