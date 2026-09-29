/** 仅美化完整 JSON 正文；不提取字段、不渲染 Markdown，非 JSON 保持原样。 */
export function formatJsonResponse(body: string): string {
  try {
    JSON.parse(body) // 只校验语法，不使用解析后的数值，避免大整数和小数精度丢失。
    const tokens = body.match(/"(?:\\.|[^"\\])*"|[{}[\],:]|[^\s{}[\],:]+/g)!
    const output: string[] = []
    let depth = 0
    const newline = () => output.push('\n', '  '.repeat(depth))
    for (let index = 0; index < tokens.length; index++) {
      const token = tokens[index]!
      switch (token) {
        case '{':
        case '[':
          output.push(token)
          depth++
          if (tokens[index + 1] !== (token === '{' ? '}' : ']')) newline()
          break
        case '}':
        case ']':
          depth--
          if (tokens[index - 1] !== (token === '}' ? '{' : '[')) newline()
          output.push(token)
          break
        case ',':
          output.push(token)
          newline()
          break
        case ':':
          output.push(': ')
          break
        default:
          // 单独解码字符串，将 \u 中文显示为文字，仍保留引号、换行等必要 JSON 转义。
          output.push(token.startsWith('"') ? JSON.stringify(JSON.parse(token)) : token)
      }
    }
    return output.join('')
  } catch {
    return body
  }
}
