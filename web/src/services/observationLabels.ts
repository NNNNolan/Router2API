export const privacyCategories: Record<string, string> = {
  api_key: 'API Key', aws_key: 'AWS Key', jwt: 'JWT', private_key: '私钥', email: '邮箱', phone: '电话',
  intranet: '内网地址', credit_card: '银行卡', national_id: '身份证件', credential: '明文凭据', custom: '自定义规则',
}
export const privacyActions = [
  { label: '关闭', value: 'off' }, { label: '告警', value: 'warn' }, { label: '拦截', value: 'block' }, { label: '脱敏并还原', value: 'redact' },
]
export const stageLabels: Record<string, string> = {
  privacy: '隐私检查', routing: '路由解析', 'mapping.target': '映射目标', 'account.selection': '账号选择',
  'account.lease': '账号租用', 'proxy.selection': '代理选择', 'plugin.attempt': '插件尝试', 'response.restore': '响应还原',
}
export const outcomeLabels: Record<string, string> = {
  completed: '完成', failed: '失败', cancelled: '取消', blocked: '拦截', dry_run: '仅检测',
  disabled: '未启用', unavailable: '不可用', cooldown: '冷却跳过', direct: '直连',
}
export const endpointLabel = (endpoint: string | null) => ({
  '/v1/chat/completions': 'Chat Completions', '/v1/completions': 'Completions', '/v1/responses': 'Responses',
  '/v1/messages': 'Messages', '/v1/audio/speech': '语音合成',
}[endpoint ?? ''] ?? endpoint ?? '旧请求')
