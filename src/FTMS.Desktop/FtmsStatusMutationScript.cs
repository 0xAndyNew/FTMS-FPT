namespace FTMS.Desktop;

internal static class FtmsStatusMutationScript
{
    internal const string Value = """
        (() => {
          if (window !== window.top || window.__ftmsCompanionStatusMutationInstalled) return;
          window.__ftmsCompanionStatusMutationInstalled = true;

          const nativeFetch = window.fetch;
          const NativeXMLHttpRequest = window.XMLHttpRequest;
          const statusEndpointPattern = /^\/ihub\/(?:request|incident|case|problem|cr|changerequest|[a-z0-9_-]+)\/(?:changestatus|updatestatus|closeticket|close)(?:v\d+)?\/?$/i;
          const assignmentEndpointPattern = /^\/ihub\/(?:request|incident|case|problem|cr|changerequest|[a-z0-9_-]+)\/(?:takeandassignment|assign|assignment)(?:v\d+)?\/?$/i;
          const endpointType = value => {
            try {
              const url = new URL(String(value || ''), location.href);
              if (url.origin !== location.origin) return null;
              if (statusEndpointPattern.test(url.pathname)) return 'status';
              if (assignmentEndpointPattern.test(url.pathname)) return 'assignment';
            } catch { }
            return null;
          };
          const entriesOf = body => {
            const entries = [];
            try {
              if (body instanceof FormData || body instanceof URLSearchParams)
                body.forEach((value, key) => entries.push([key, value]));
              else if (typeof body === 'string' && /^\s*\{/.test(body)) {
                const payload = JSON.parse(body);
                for (const [key, value] of Object.entries(payload || {})) {
                  if (key.toLowerCase() === 'input' && value && typeof value === 'object')
                    for (const [nestedKey, nestedValue] of Object.entries(value))
                      entries.push([`input[${nestedKey}]`, nestedValue]);
                  else entries.push([key, value]);
                }
              } else if (typeof body === 'string')
                new URLSearchParams(body).forEach((value, key) => entries.push([key, value]));
            } catch { }
            return entries;
          };
          const read = (entries, names) => {
            const expected = new Set(names.map(name => name.toLowerCase()));
            const match = entries.find(([key]) => expected.has(String(key || '').toLowerCase()));
            return match ? String(match[1] ?? '').trim() : '';
          };
          const statusOf = value => {
            const number = Number(value);
            if ([1, 2, 3, 4, 5, 7, 8].includes(number)) return number;
            const text = String(value || '').normalize('NFD').replace(/[̀-ͯ]/g, '')
              .replace(/[đĐ]/g, 'd').toLowerCase().replace(/\s+/g, ' ').trim();
            if (/phan cong|assign/.test(text)) return 1;
            if (/dang thuc hien|dang xu ly|in progress/.test(text)) return 2;
            if (/hoan thanh|complete/.test(text)) return 3;
            if (/tam ngung|tam dung|pause|pending/.test(text)) return 4;
            if (/da dong|\bdong\b|closed/.test(text)) return 5;
            return 0;
          };
          const succeeded = payload => {
            if (!payload) return false;
            if (typeof payload === 'string') {
              try { payload = JSON.parse(payload); } catch { return /thành công|success|completed|ok/i.test(payload); }
            }
            if (typeof payload !== 'object' || Array.isArray(payload)) return false;
            const p = k => payload[k] ?? payload[k.toLowerCase()] ?? payload[k.toUpperCase()] ??
              payload[k.charAt(0).toUpperCase() + k.slice(1)];
            const success = p('success'), result = p('result'), ok = p('ok'), err = p('error');
            const code = Number(p('code')), status = p('status');
            if (success === false || result === false || ok === false || code >= 400 || err) return false;
            const message = String(payload.message?.value ?? payload.message ?? payload.Message ?? '');
            return code === 200 || status === 200 || Number(status) === 200 ||
              success === true || result === true || result === 1 || String(result) === '1' || String(result).toLowerCase() === 'true' ||
              ok === true || String(status || '').toLowerCase() === 'ok' ||
              /cap nhat thanh cong|cập nhật thành công|thành công|success|completed|done/i.test(message);
          };
          const notify = (url, body, payload) => {
            try {
              if (!succeeded(payload)) return;
              const mutationType = endpointType(url);
              const entries = entriesOf(body);
              const code = read(entries, ['code','input[code]','ticketCode','requestCode','caseCode']) ||
                location.pathname.match(/(?:RQ|CA|IN|PR|CR)[A-Z0-9_-]+/i)?.[0] || '';
              if (!/^(?:RQ|CA|IN|PR|CR)[A-Z0-9_-]+$/i.test(code)) return;
              const actor = read(entries, ['staffName','creator','input[staffName]','input[creator]','updatedBy']) ||
                String(globalThis.Username || '');
              if (mutationType === 'assignment') {
                const assigneeId = Number(read(entries, ['staffId','staff_ID','input[staffId]','input[staff_ID]',
                  'assigneeId','agentId']) || (typeof globalThis.userID !== 'undefined' ? globalThis.userID : 0));
                const assigneeName = read(entries, ['staffName','input[staffName]','assigneeName']) || actor;
                const status = statusOf(read(entries, ['input[ticketStatus]','ticketStatus','status','statusCode']));
                if (!Number.isInteger(assigneeId) || assigneeId <= 0) return;
                window.chrome?.webview?.postMessage({
                  type: 'ftms-assignment-mutation', code: code.toUpperCase(), assigneeId, assigneeName, actor,
                  status: status || undefined,
                  detectedAt: Date.now()
                });
                return;
              }
              const status = statusOf(read(entries, ['status','statusCode','statusId','newStatus','targetStatus',
                'input[status]','input[statusCode]','input[ticketStatus]','ticketStatus','requestStatus']));
              const previousStatus = statusOf(read(entries, ['oldStatus','oldStatusCode','previousStatus',
                'previousStatusCode','fromStatus','input[oldStatus]','input[oldStatusCode]']));
              if (!status) return;
              window.chrome?.webview?.postMessage({
                type: 'ftms-status-mutation', code: code.toUpperCase(), status, previousStatus, actor,
                detectedAt: Date.now()
              });
            } catch { }
          };

          if (typeof nativeFetch === 'function') {
            window.fetch = function(input, init) {
              let tracked = null;
              try {
                const url = typeof input === 'string' || input instanceof URL ? String(input) : String(input?.url || '');
                const method = String(init?.method || input?.method || 'GET').toUpperCase();
                if (method === 'POST' && endpointType(url)) {
                  const body = init?.body !== undefined ? Promise.resolve(init.body) :
                    input?.clone ? input.clone().text().catch(() => undefined) : Promise.resolve(undefined);
                  tracked = { url, body };
                }
              } catch { }
              const promise = nativeFetch.apply(this, arguments);
              if (!tracked) return promise;
              return promise.then(response => {
                if (response.ok) (async () => {
                  let payload = null;
                  try {
                    if (!response.url || endpointType(response.url)) payload = JSON.parse(await response.clone().text());
                  } catch { }
                  notify(tracked.url, await tracked.body, payload);
                })().catch(() => {});
                return response;
              });
            };
          }

          if (typeof NativeXMLHttpRequest === 'function') {
            const nativeOpen = NativeXMLHttpRequest.prototype.open;
            const nativeSend = NativeXMLHttpRequest.prototype.send;
            NativeXMLHttpRequest.prototype.open = function(method, url) {
              const result = nativeOpen.apply(this, arguments);
              try { this.__ftmsMethod = String(method || '').toUpperCase(); this.__ftmsUrl = String(url || ''); } catch { }
              return result;
            };
            NativeXMLHttpRequest.prototype.send = function(body) {
              try {
                if (this.__ftmsMethod === 'POST' && endpointType(this.__ftmsUrl)) {
                  this.addEventListener('loadend', () => {
                    try {
                      if (this.status < 200 || this.status >= 300) return;
                      let payload = null;
                      try { payload = this.responseType === 'json' ? this.response : JSON.parse(this.responseText); } catch { }
                      notify(this.__ftmsUrl, body, payload);
                    } catch { }
                  }, { once: true });
                }
              } catch { }
              return nativeSend.apply(this, arguments);
            };
          }
        })();
        """;
}
