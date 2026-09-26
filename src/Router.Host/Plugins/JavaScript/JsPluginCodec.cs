using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Acornima.Ast;
using Jint;
using Jint.Runtime;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Host.Plugins.JavaScript;

internal static class JsPluginCodec
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 48, Converters = { new JsonStringEnumConverter() }
    };
    public static readonly JsonSerializerOptions AttemptJsonOptions = new(JsonOptions)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static object BuildContext(JsPluginManifest manifest, JsCallContext context, string? originalBodyHandle) => new
    {
        pluginKey = manifest.Id,
        generationId = manifest.GenerationId,
        pluginVersion = manifest.Version,
        platform = manifest.Platform.Name,
        invocationId = Guid.NewGuid().ToString("N"),
        traceId = context.Attempt?.TraceId,
        query = context.Endpoint?.Query,
        body = context.Endpoint?.Body,
        task = context.TaskName,
        job = context.Job is { } job ? new { id = job.Id } : null,
        phase = context.Kind.ToString(),
        request = (context.Attempt?.Request ?? context.SelectionRequest) is { } request ? new
        {
            request.Model, request.Endpoint, request.Stream, request.MaxTokens, request.Temperature,
            request.Messages, request.Tools, request.Extensions, headers = request.RequestHeaders,
            originalBodyRef = originalBodyHandle
        } : null,
        account = (context.Attempt?.Account ?? context.CredentialAccount) is { } account ? AccountMetadata(account) : null
    };

    public static JsonNode? CredentialJson(Credential credential) => JsonSerializer.SerializeToNode(credential, credential.GetType(), JsonOptions);

    public static object AccountMetadata(Account account, bool includeCredential = false) => new
    {
        account.Id, account.Platform, account.Label, account.ExpiresAt,
        credentialKind = account.Credential.Kind.ToString(),
        credentialVersion = account.CredentialVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        credential = includeCredential ? CredentialJson(account.Credential) : null,
        credentialMetadata = account.Credential is OAuthCredential oauth
            ? (object)new { oauth.Domain, oauth.AccountId, oauth.EnterpriseId, oauth.Nickname }
            : account.Credential is BasicAuthCredential basic ? new { basic.Username } : null,
        status = new
        {
            state = account.Status.State.ToString(), account.Status.CooldownUntil, account.Status.DisabledUntil,
            account.Status.Reason, account.Status.LastStatusCode, account.Status.ConsecutiveFailures
        }
    };

    public static string SafeError(Exception exception, Account? account)
    {
        var message = exception.Message;
        IEnumerable<string?> secrets = account?.Credential switch
        {
            ApiKeyCredential key => [key.ApiKey],
            OAuthCredential oauth => [oauth.AccessToken, oauth.RefreshToken, oauth.IdToken],
            BearerTokenCredential bearer => [bearer.Token],
            BasicAuthCredential basic => [basic.Password],
            CookieCredential cookie => [cookie.Cookie],
            CustomCredential custom => custom.Fields.Values,
            _ => []
        };
        foreach (var secret in secrets.Where(value => !string.IsNullOrEmpty(value)))
            message = message.Replace(secret!, "[redacted]", StringComparison.Ordinal);
        return message.Length > 1000 ? message[..1000] : message;
    }

    public static readonly Prepared<Script> Bootstrap = Engine.PrepareScript("""
        "use strict";
        function __validate(namesJson) {
          for (const name of JSON.parse(namesJson))
            if (!Object.hasOwn(__plugin, name) || typeof __plugin[name] !== "function") throw new Error("Missing JS export: " + name);
        }
        async function __call(name, metadataJson, inputJson) {
          const encode = value => JSON.stringify(value, (key, item) => {
            if (typeof item === "number" && !Number.isFinite(item)) throw new Error("Non-finite JSON number.");
            if (typeof item === "bigint" || typeof item === "function" || typeof item === "symbol"
                || item instanceof Map || item instanceof Set) throw new Error("Only JSON values may cross the host boundary.");
            return item;
          });
          const unwrap = json => {
            const result = JSON.parse(json);
            if (!result.ok) { const error = new Error(result.error.message); error.code = result.error.code; throw error; }
            return result.value;
          };
          const call = async (operation, input = {}) => unwrap(await __hostCall(operation, encode(input)));
          const sync = (operation, input = {}) => unwrap(__hostSync(operation, encode(input)));
          const stateApi = scope => Object.freeze({
            available: () => call("state.available", {scope}),
            get: key => call("state.get", {scope,key}),
            getString: key => call("state.getString", {scope,key}),
            set: async (key,value,options) => { await call("state.set", {scope,key,value,ttlSeconds:options?.ttlSeconds}); },
            setString: async (key,value,options) => { await call("state.setString",{scope,key,value,ttlSeconds:options?.ttlSeconds}); },
            remove: key => call("state.remove", {scope,key}),
            expiry: key => call("state.expiry", {scope,key}),
            putIfAbsent: (key,value,options) => call("state.putIfAbsent", {scope,key,value,ttlSeconds:options?.ttlSeconds}),
            increment: (key,delta="1",options) => {
              if(typeof delta==="number" && !Number.isSafeInteger(delta)) throw new Error("Counter delta must be a safe integer or string.");
              return call("state.increment", {scope,key,delta:String(delta),ttlSeconds:options?.ttlSeconds});
            },
            compareExchange: (key,expected,value,options) => call("state.compareExchange",
              {scope,key,expected,value,missing:expected===undefined,remove:value===undefined,ttlSeconds:options?.ttlSeconds})
          });
          const local=stateApi("local"), shared=stateApi("shared");
          const ctx = Object.assign(JSON.parse(metadataJson), {
            http: Object.freeze({
              request: spec => call("http.request", spec),
              open: spec => call("http.open", spec),
              createClient: async options => {
                const {handle}=await call("http.createClient",options);
                return Object.freeze({
                  handle,
                  request: spec => call("http.request",{...spec,client:handle}),
                  open: spec => call("http.open",{...spec,client:handle}),
                  close: () => call("http.closeClient",{handle})
                });
              },
              readText: handle => call("http.readText", {handle}),
              readJson: handle => call("http.readJson", {handle}),
              readBase64: handle => call("http.readBase64", {handle}),
              drain: handle => call("http.drain", {handle}),
              snapshotError: handle => call("http.snapshotError", {handle}),
              close: handle => call("http.close", {handle}),
              approvedOrigins: () => call("http.approvedOrigins"),
              approveOrigin: origin => call("http.approveOrigin",{origin}),
              revokeOrigin: origin => call("http.revokeOrigin",{origin})
            }),
            state: Object.freeze({...local,local,shared}),
            accounts: Object.freeze({
              ensureAnonymous: () => call("accounts.ensureAnonymous"),
              currentCredential: () => call("accounts.currentCredential"),
              list: options => call("accounts.list",options),
              get: id => call("accounts.get",{id}),
              save: account => call("accounts.save",account),
              delete: id => call("accounts.delete",{id}),
              readCredentials: id => call("accounts.readCredentials",{id}),
              compareExchangeCredential: (id,expectedVersion,credential) => call("accounts.compareExchangeCredential",{id,expectedVersion,credential}),
              refresh: id => call("accounts.refresh",{id}),
              setCooldown: (id,until,reason,statusCode) => call("accounts.setCooldown",{id,until,reason,statusCode}),
              clearCooldown: (id,expectedReason) => call("accounts.clearCooldown",{id,expectedReason}),
              disable: (id,reason,statusCode) => call("accounts.disable",{id,reason,statusCode})
            }),
            models: Object.freeze({
              list: platform => call("models.list",{platform}),
              metadata: () => call("models.metadata"),
              refresh: platform => call("models.refresh",{platform}),
              invalidate: platform => call("models.invalidate",{platform})
            }),
            tasks: Object.freeze({
              run: name => call("tasks.run", {name}),
              writeLog: entry => call("tasks.writeLog",entry)
            }),
            jobs: Object.freeze({
              start: (name,input,options) => call("jobs.start",{name,input,...options}),
              get: id => call("jobs.get",{id}),
              list: () => call("jobs.list"),
              cancel: id => call("jobs.cancel",{id}),
              wait: id => call("jobs.wait",{id}),
              progress: value => call("jobs.progress",{value})
            }),
            crypto: Object.freeze({
              randomUUID: () => sync("crypto.randomUUID"),
              hash: (algorithm,text) => sync("crypto.hash",{algorithm,text}),
              sha256: text => sync("crypto.hash",{algorithm:"SHA256",text}),
              hmacSha256: (key,text) => sync("crypto.hmac",{key,text})
            }),
            encoding: Object.freeze({
              toBase64: text => sync("encoding.base64Encode",{text}),
              fromBase64: text => sync("encoding.base64Decode",{text}),
              hexToBase64: text => sync("encoding.hexToBase64",{text})
            }),
            decimal: Object.freeze({
              add: (left,right) => sync("decimal.add",{left,right}),
              subtract: (left,right) => sync("decimal.subtract",{left,right}),
              multiply: (left,right) => sync("decimal.multiply",{left,right}),
              divide: (left,right) => sync("decimal.divide",{left,right}),
              compare: (left,right) => sync("decimal.compare",{left,right})
            }),
            url: Object.freeze({
              parse: text => sync("url.parse",{text}),
              resolve: (base,text) => sync("url.resolve",{base,text})
            }),
            reply: Object.freeze({
              completion: completion => ({response:{kind:"completion",statusCode:200,completion},attempt:{decision:{failureKind:"None"}}}),
              error: (statusCode,message,decision={failureKind:"Upstream"}) => ({response:{kind:"error",statusCode,message},attempt:{statusCode,decision}}),
              raw: (source,decision={failureKind:"None"}) => ({response:{kind:"raw",source:source.handle},attempt:{statusCode:source.statusCode,decision}}),
              mappedStream: (source,mapper,state) => ({response:{kind:"mappedStream",statusCode:source.statusCode,source:source.handle,mapper,state},attempt:{decision:{failureKind:"None"}}})
            }),
            log: Object.freeze({ write: entry => call("log.write", entry) }),
            delay: milliseconds => call("delay", {milliseconds}),
            json: (statusCode, body) => ({statusCode, body, contentType: "application/json"})
          });
          const value = await __plugin[name](ctx, JSON.parse(inputJson));
          return encode(value ?? null);
        }
        function __map(name, inputJson, stateJson) {
          const value = __plugin[name](JSON.parse(inputJson), JSON.parse(stateJson));
          if (value && typeof value.then === "function") throw new Error("Stream mappers must be synchronous.");
          return JSON.stringify(value);
        }
        function __finalize(name, inputJson) {
          const value = __plugin[name](JSON.parse(inputJson));
          if (value && typeof value.then === "function") throw new Error("Completion mappers must be synchronous.");
          return JSON.stringify(value);
        }
        """, source: "router2api-sdk-bootstrap.js", strict: true);

}

internal enum JsCallKind { Terminal, Control, Task, Job, Selection, Callback, Stop }
internal sealed record JsCallContext(JsCallKind Kind, TimeSpan Timeout,
    PluginAttemptContext? Attempt = null, PluginHttpContext? Endpoint = null, string? TaskName = null, bool Starting = false,
    AdapterRequest? SelectionRequest = null, Account? CredentialAccount = null,
    Router.Contracts.Plugins.PluginJobContext? Job = null, Credential? SecretCredential = null, CancellationToken Token = default);
