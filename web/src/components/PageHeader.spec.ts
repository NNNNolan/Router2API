import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import PageHeader from './PageHeader.vue'

describe('PageHeader', () => {
  it('renders the page title, description and action slot', () => {
    const wrapper = mount(PageHeader, {
      props: {
        eyebrow: '测试',
        title: '页面标题',
        description: '页面说明',
      },
      slots: { actions: '<button aria-label="新增">新增</button>' },
    })

    expect(wrapper.get('h2').text()).toBe('页面标题')
    expect(wrapper.text()).toContain('页面说明')
    expect(wrapper.get('button').attributes('aria-label')).toBe('新增')
  })
})
