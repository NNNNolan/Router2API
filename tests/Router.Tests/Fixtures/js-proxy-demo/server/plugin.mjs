export async function start(ctx) {
  await ctx.accounts.ensureAnonymous();
}

export function getModels() {
  return [{ id: "echo", displayName: "Jint Echo", supportsStreaming: true }];
}

export function invoke(ctx) {
  const message = ctx.request.messages.at(-1)?.content || "";
  return ctx.reply.completion({ model: ctx.request.model, content: `Jint: ${message}`, finishReason: "stop" });
}

export async function checkPool(ctx) {
  if (ctx.job) await ctx.jobs.progress({ stage: "requesting" });
  const response = await ctx.http.request({
    method: "GET",
    url: "https://example.com",
    route: "pool",
    responseType: "text",
    timeoutMs: 8000,
    retry: { maxRetries: 1, delayMs: 200 }
  });
  // The HTTP factory does not interpret status codes. Business interpretation belongs here.
  const snapshot = {
    statusCode: response.statusCode,
    checkedAt: new Date().toISOString(),
    ok: response.statusCode >= 200 && response.statusCode < 300
  };
  await ctx.state.set("last-probe", snapshot, { ttlSeconds: 86400 });
  return snapshot;
}

export async function status(ctx) {
  if (ctx.query?.jobId) return ctx.json(200, await ctx.jobs.get(ctx.query.jobId));
  return ctx.json(200, { lastProbe: await ctx.state.get("last-probe") });
}

export async function probe(ctx) {
  return ctx.json(202, await ctx.jobs.start("js-proxy-demo-probe", null, { key: "manual-probe" }));
}
