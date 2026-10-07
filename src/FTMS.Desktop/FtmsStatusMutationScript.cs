namespace FTMS.Desktop;

internal static class FtmsStatusMutationScript
{
    internal const string Value = """
        (() => {
          if (window.__ftmsCompanionStatusMutationInstalled) return;
          window.__ftmsCompanionStatusMutationInstalled = true;

          const nativeFetch = window.fetch;
          const NativeXMLHttpRequest = window.XMLHttpRequest;
          const statusEndpointPattern = /^\/ihub\/(?:.+?\/)?(?:change[-_]?status|update[-_]?status|close[-_]?ticket|close|pending[-_]?customer|pause[-_]?ticket|pause|resume[-_]?ticket|resume|reopen|resolve)(?:v\d+)?\/?$/i;
          const assignmentEndpointPattern = /^\/ihub\/(?:.+?\/)?(?:take[-_]?and[-_]?assignment|assign|assignment|take)(?:v\d+)?\/?$/i;
          const actionEndpointPattern = /^\/ihub\/(?:.+?\/)?(?:sendmail|sendemail|reply|replymail|save|update|edit|action|process|submit)(?:v\d+)?\/?$/i;
          const endpointType = value => {
            try {
              const url = new URL(String(value || ''), location.href);
              if (url.origin !== location.origin) return null;
              if (statusEndpointPattern.test(url.pathname)) return 'status';
              if (assignmentEndpointPattern.test(url.pathname)) return 'assignment';
              if (actionEndpointPattern.test(url.pathname)) return 'action';
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
            if (value === null || value === undefined || String(value).trim() === '') return null;
            const number = Number(value);
            if ([0, 1, 2, 3, 4, 5, 7, 8].includes(number)) return number;
            const text = String(value).normalize('NFD').replace(/[̀-ͯ]/g, '')
              .replace(/[đĐ]/g, 'd').toLowerCase().replace(/\s+/g, ' ').trim();
            if (/tao moi|\bmoi\b|\bnew\b/.test(text)) return 0;
            if (/phan cong|assign/.test(text)) return 1;
            if (/dang thuc hien|dang xu ly|in progress/.test(text)) return 2;
            if (/hoan thanh|complete/.test(text)) return 3;
            if (/tam ngung|tam dung|pause|pending/.test(text)) return 4;
            if (/da dong|\bdong\b|closed/.test(text)) return 5;
            if (/da huy|\bhuy\b|cancel/.test(text)) return 7;
            if (/khong xu ly|unprocessed/.test(text)) return 8;
            return null;
          };
          const succeeded = (payload, httpStatus) => {
            if (httpStatus !== undefined && (httpStatus < 200 || httpStatus >= 300)) return false;
            if (payload === null || payload === undefined || payload === '') return true;
            if (typeof payload === 'string') {
              try { payload = JSON.parse(payload); }
              catch { return !/error|thất bại|failed|exception/i.test(payload); }
            }
            if (typeof payload !== 'object' || Array.isArray(payload)) return true;
            const p = k => payload[k] ?? payload[k.toLowerCase()] ?? payload[k.toUpperCase()] ??
              payload[k.charAt(0).toUpperCase() + k.slice(1)];
            const success = p('success'), result = p('result'), ok = p('ok'), err = p('error');
            const code = Number(p('code'));
            if (success === false || result === false || ok === false || err) return false;
            if (code >= 400) return false;
            return true;
          };
          const notify = (url, body, payload, httpStatus) => {
            try {
              if (!succeeded(payload, httpStatus)) return;
              const mutationType = endpointType(url);
              const entries = entriesOf(body);
              const code = read(entries, ['code','input[code]','ticketCode','requestCode','caseCode','ticketNo','requestNo']) ||
                location.pathname.match(/(?:RQ|CA|IN|PR|CR)[A-Z0-9_-]+/i)?.[0] ||
                new URL(String(url || ''), location.href).searchParams.get('code') ||
                new URL(location.href).searchParams.get('code') || '';
              if (!/^(?:RQ|CA|IN|PR|CR)[A-Z0-9_-]+$/i.test(code)) {
                if (mutationType) {
                  window.chrome?.webview?.postMessage({
                    type: 'ftms-general-mutation',
                    detectedAt: Date.now()
                  });
                }
                return;
              }
              const actor = read(entries, ['staffName','creator','input[staffName]','input[creator]','updatedBy','updateStaffName']) ||
                String(globalThis.FullName || globalThis.StaffName || globalThis.Username || '');
              const note = read(entries, ['statusChangeNote','input[statusChangeNote]','comment','input[comment]',
                'note','input[note]','reason','input[reason]','content','input[content]','message','description']) || null;

              if (mutationType === 'assignment') {
                const assigneeId = Number(read(entries, ['staffId','staff_ID','input[staffId]','input[staff_ID]',
                  'assigneeId','agentId']) || (typeof globalThis.userID !== 'undefined' ? globalThis.userID : 0));
                const assigneeName = read(entries, ['staffName','input[staffName]','assigneeName']) || actor;
                const status = statusOf(read(entries, ['input[ticketStatus]','ticketStatus','status','statusCode']));
                if (!Number.isInteger(assigneeId) || assigneeId <= 0) return;
                window.chrome?.webview?.postMessage({
                  type: 'ftms-assignment-mutation', code: code.toUpperCase(), assigneeId, assigneeName, actor,
                  status: status ?? undefined,
                  note: note || undefined,
                  detectedAt: Date.now()
                });
                return;
              }

              const status = statusOf(read(entries, ['status','statusCode','statusId','newStatus','targetStatus',
                'statusChangeId','input[status]','input[statusCode]','input[statusChangeId]','input[ticketStatus]',
                'ticketStatus','requestStatus']));
              const previousStatus = statusOf(read(entries, ['oldStatus','oldStatusCode','previousStatus',
                'previousStatusCode','fromStatus','input[oldStatus]','input[oldStatusCode]']));

              if (status !== null) {
                window.chrome?.webview?.postMessage({
                  type: 'ftms-status-mutation', code: code.toUpperCase(), status, previousStatus, actor,
                  note: note || undefined,
                  detectedAt: Date.now()
                });
                return;
              }

              window.chrome?.webview?.postMessage({
                type: 'ftms-general-mutation', code: code.toUpperCase(), actor,
                note: note || undefined,
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
                  let payload = '';
                  try {
                    if (!response.url || endpointType(response.url)) {
                      const text = await response.clone().text();
                      payload = text ? JSON.parse(text) : '';
                    }
                  } catch { }
                  notify(tracked.url, await tracked.body, payload, response.status);
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
                      let payload = '';
                      try {
                        payload = this.responseType === 'json' ? this.response :
                          this.responseText ? JSON.parse(this.responseText) : '';
                      } catch { }
                      notify(this.__ftmsUrl, body, payload, this.status);
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
