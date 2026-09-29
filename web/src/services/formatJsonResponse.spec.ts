// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { formatJsonResponse } from './formatJsonResponse'

describe('测试响应 JSON 美化', () => {
  it('缩进嵌套对象和数组，解码中文键与值，不提取字段', () => {
    expect(formatJsonResponse(String.raw`{"\u6d88\u606f":"\u4f60\u597d","items":[{"ok":true},null,[],{}]}`)).toBe(
      '{\n  "消息": "你好",\n  "items": [\n    {\n      "ok": true\n    },\n    null,\n    [],\n    {}\n  ]\n}',
    )
  })

  it('保留数值原文、重复字段及字段顺序', () => {
    expect(formatJsonResponse('{"2":9007199254740993,"1":1.234567890123456789,"1":1e400,"zero":-0}')).toBe(
      '{\n  "2": 9007199254740993,\n  "1": 1.234567890123456789,\n  "1": 1e400,\n  "zero": -0\n}',
    )
  })

  it('字符串内的标点、转义和嵌套 JSON 不当作外层结构解析', () => {
    const text = String.raw`{"text":"a,b:[{}] \"quoted\"\nline\tend","literal":"\\u4f60","nested":"{\"a\":1}","emoji":"\ud83d\ude00"}`
    expect(formatJsonResponse(text)).toBe(
      '{\n  "text": "a,b:[{}] \\"quoted\\"\\nline\\tend",\n  "literal": "\\\\u4f60",\n  "nested": "{\\"a\\":1}",\n  "emoji": "😀"\n}',
    )
  })

  it.each(['', 'upstream failed\n', '<script>alert(1)</script>', 'data: {"a":1}\n\n', '{"a":}', '{"a":1,}'])(
    '非 JSON / 错误 JSON 保持原文 %s', body => expect(formatJsonResponse(body)).toBe(body),
  )

  it.each([['{}', '{}'], [' [ ] ', '[]'], ['null', 'null'], ['9007199254740993', '9007199254740993'],
    [String.raw`"\u4f60\u597d"`, '"你好"']])(
    '支持顶层 JSON 值 %s', (body, expected) => expect(formatJsonResponse(body)).toBe(expected),
  )
})
