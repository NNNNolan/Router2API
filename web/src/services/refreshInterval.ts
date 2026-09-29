export function isValidRefreshInterval(value: string): boolean {
  const match = /^([1-9]\d*)([HMS])$/i.exec(value.trim())
  if (!match) return false
  const multiplier = { H: 3600, M: 60, S: 1 }[match[2]!.toUpperCase()]!
  const seconds = Number(match[1]) * multiplier
  return Number.isSafeInteger(seconds) && seconds <= 2147483647 * 60
}

export function formatRefreshInterval(seconds: number): string {
  if (seconds % 3600 === 0) return `${seconds / 3600}H`
  if (seconds % 60 === 0) return `${seconds / 60}M`
  return `${seconds}S`
}
