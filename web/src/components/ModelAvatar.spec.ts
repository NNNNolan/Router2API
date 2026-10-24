import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import ModelAvatar from './ModelAvatar.vue'

describe('模型头像', () => {
  it('图标加载失败后显示 DS，容器与可访问名称保持不变', async () => {
    const view = mount(ModelAvatar, { props: { modelId: 'DeepSeek-R1' } })
    expect(view.attributes('aria-label')).toBe('DeepSeek')
    await view.get('img').trigger('error')
    expect(view.find('img').exists()).toBe(false)
    expect(view.text()).toBe('DS')
    expect(view.classes()).toContain('model-avatar')
    await view.setProps({ modelId: 'DeepSeek-V3' })
    expect(view.find('img').exists()).toBe(true)
    await view.setProps({ modelId: 'Qwen3' })
    expect(view.find('img').exists()).toBe(true)
    view.unmount()
  })
})
