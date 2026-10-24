import { describe, expect, it } from 'vitest'
import { modelInitials, resolveModelIdentity } from './modelIdentity'

describe('模型身份和缩写', () => {
  it.each([
    ['deepseek-ai/DeepSeek-R1', 'DeepSeek', 'DS'],
    ['proxy/QWEN3-32B', 'Qwen', 'QW'],
    ['deepseek-r1-distill-qwen-32b', 'DeepSeek', 'DS'],
    ['nvidia/Llama-3.1-Nemotron', 'NVIDIA', 'NV'],
    ['claude-sonnet-4-5', 'Claude', 'CL'],
    ['gpt-4o', 'OpenAI', 'OA'],
    ['o3-mini', 'OpenAI', 'OA'],
    ['gemma2-9b', 'Gemini', 'GM'],
    ['moonshotai/Kimi-K3', 'Kimi', 'KM'],
    ['zai-org/GLM-5', 'GLM', 'GL'],
    ['grok-4', 'Grok', 'GK'],
    ['mistralai/Mixtral-8x7B', 'Mistral', 'MI'],
    ['MiniMax/MiniMax-M2', 'MiniMax', 'MM'],
    ['meta-llama/Llama-3.3', 'Llama', 'LM'],
    ['gpt-4.1-2025-04-14', 'OpenAI', 'OA'],
    ['o4-mini', 'OpenAI', 'OA'],
    ['ClaudeSonnet-4', 'Claude', 'CL'],
    ['Claude-Distill-Qwen-32B', 'Claude', 'CL'],
  ])('%s 识别模型品牌', (id, name, initials) => {
    expect(resolveModelIdentity(id)).toMatchObject({ name, initials, icon: expect.any(String) })
  })

  it('不把转发平台或单词中的子串当作品牌', () => {
    expect(resolveModelIdentity('openai/CustomModel-v2', 'CustomModel-v2', 'openai')).toEqual({ name: 'CustomModel-v2', initials: 'CM' })
    expect(resolveModelIdentity('notqwenmodel').icon).toBeUndefined()
    expect(resolveModelIdentity('custom/id', 'DeepSeek xxxx').initials).toBe('DS')
    expect(resolveModelIdentity('代理平台/qwen3-32b', '', '代理平台').name).toBe('Qwen')
    expect(resolveModelIdentity('CLAUDE/CustomModel-v2', 'CustomModel-v2', 'claude').initials).toBe('CM')
    expect(resolveModelIdentity('anthropic/custom').name).toBe('Claude')
    expect(resolveModelIdentity('DeepSeek-R1', 'Qwen3').name).toBe('DeepSeek')
    expect(resolveModelIdentity('CustomModel-v2')).toEqual({ name: 'CustomModel-v2', initials: 'CM' })
  })

  it.each([['DeepSeek xxxx', 'DS'], ['CustomModel-v2', 'CM'], ['XMLParser-v2', 'XP'], ['custom_model', 'CM'], ['custom', 'CU'], ['ßeta', 'SS'], ['中文模型', '中文'], ['', '?']])('%s 的后备文字为 %s', (name, initials) => {
    expect(modelInitials(name)).toBe(initials)
  })
})
