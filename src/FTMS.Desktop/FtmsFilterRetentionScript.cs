namespace FTMS.Desktop;

internal static class FtmsFilterRetentionScript
{
    internal const string Value = """
        (() => {
          if (window.__ftmsCompanionFilterRetentionInstalled) return;
          window.__ftmsCompanionFilterRetentionInstalled = true;

          const norm = str => String(str || '').normalize('NFD')
            .replace(/[̀-ͯ]/g, '')
            .replace(/[đĐ]/g, 'd')
            .toLowerCase()
            .trim();

          let manualOverride = false;
          try {
            manualOverride = sessionStorage.getItem('ftms_companion_manual_override') === 'true';
          } catch { }

          let myTicketActive = false;

          const shouldRetainStatus = () => !manualOverride && !myTicketActive;

          const isMyTicketActive = btn => {
            if (!btn) return false;
            if (btn.classList.contains('ant-switch-checked')) return true;
            if (btn.getAttribute('aria-checked') === 'true') return true;
            if (btn.getAttribute('aria-pressed') === 'true') return true;
            if (btn.classList.contains('active') || btn.classList.contains('selected') || btn.classList.contains('ant-btn-primary')) return true;
            if (btn.querySelector('.ant-switch-checked, [aria-checked="true"], .active, .selected')) return true;
            return false;
          };

          const isMyTicketButton = el => {
            if (!el) return false;
            if (el.closest('header, nav, .main-header, .navbar, #header, .header')) return false;

            const btn = el.closest('button, [role="button"], a, .btn, .ant-switch, [role="switch"], label, [class*="pointer"]');
            if (!btn) return false;
            if (btn.closest('header, nav, .main-header, .navbar, #header, .header, .dropdown-menu')) return false;

            const textProps = [
              btn.getAttribute('title'),
              btn.getAttribute('aria-label'),
              btn.getAttribute('data-tooltip'),
              btn.getAttribute('data-original-title'),
              btn.innerText
            ].map(norm).join(' ');

            if (textProps.includes('cua toi') || textProps.includes('my ticket') || textProps.includes('ca nhan'))
              return true;

            const allBtns = Array.from(document.querySelectorAll('button, [role="button"], a, .btn'));
            const vmBtn = allBtns.find(b => norm(b.innerText).includes('vung mien'));
            if (vmBtn) {
              const vmIdx = allBtns.indexOf(vmBtn);
              const btnIdx = allBtns.indexOf(btn);
              if (btnIdx > vmIdx && btnIdx <= vmIdx + 2) {
                const text = norm(btn.innerText);
                if (!text.includes('vung mien') && !text.includes('trang thai') && !text.includes('tim kiem'))
                  return true;
              }
              if (vmBtn.parentElement) {
                const siblings = Array.from(vmBtn.parentElement.querySelectorAll('button, [role="button"], a, .btn'));
                const vmSiblingIdx = siblings.indexOf(vmBtn);
                if (vmSiblingIdx !== -1 && siblings[vmSiblingIdx + 1] === btn) return true;
              }
            }

            const icon = btn.querySelector('i, svg, [class*="user"], [class*="person"], [class*="avatar"]');
            if (icon) {
              const iconBox = icon.closest('[class*="user"], [class*="person"], [class*="avatar"]') || icon;
              const cls = norm(iconBox.getAttribute('class') || '') + ' ' + norm(btn.getAttribute('class') || '');
              if (cls.includes('user') || cls.includes('person') || cls.includes('avatar') || cls.includes('profile'))
                return true;
              if (icon.tagName && icon.tagName.toLowerCase() === 'svg') {
                if (vmBtn && allBtns.indexOf(btn) > allBtns.indexOf(vmBtn) && allBtns.indexOf(btn) <= allBtns.indexOf(vmBtn) + 2)
                  return true;
              }
            }

            return false;
          };

          const isStatusToggleButton = el => {
            if (!el) return false;
            const btn = el.closest('button, [role="button"], a, .btn');
            if (!btn) return false;
            const text = norm(btn.innerText || btn.getAttribute('title') || '');
            return text.includes('trang thai') && !text.includes('vung mien');
          };

          const isStatusDropdownElement = el => {
            if (!el) return false;
            const container = el.closest('.dropdown-menu, [role="listbox"], [role="menu"], .ant-select-dropdown, .ant-dropdown, div[class*="dropdown"], div[class*="popover"]');
            if (!container) return false;
            const text = norm(container.innerText);
            return text.includes('tat ca') && text.includes('moi') && text.includes('phan cong');
          };

          const updateStatusButtonLabel = () => {
            if (!shouldRetainStatus()) return;

            const elements = Array.from(document.querySelectorAll('button, [role="button"], a, .btn, .ant-select, [class*="select"]'))
              .filter(el => !el.closest('header, nav, .main-header, .navbar, #header, .header'));

            for (const btn of elements) {
              const text = norm(btn.innerText || '');
              const isStatusBtn = (text.includes('trang thai') && !text.includes('vung mien')) ||
                                  (text === 'tat ca' || text.startsWith('tat ca (') || text.startsWith('tat ca(')) ||
                                  (btn.getAttribute('title') && norm(btn.getAttribute('title')).includes('trang thai'));
              if (!isStatusBtn) continue;

              const badge = btn.querySelector('.badge, [class*="badge"], [class*="count"], .ant-scroll-number');
              if (badge && badge.innerText !== '2') {
                badge.innerText = '2';
              }

              let updated = false;
              for (const node of btn.childNodes) {
                if (node.nodeType === Node.TEXT_NODE) {
                  const val = node.textContent || '';
                  if (/trạng\s*thái(?:\s*\([^)]*\))?/i.test(val)) {
                    node.textContent = val.replace(/trạng\s*thái(?:\s*\([^)]*\))?/i, 'Trạng thái (2)');
                    updated = true;
                  } else if (/tất\s*cả(?:\s*\([^)]*\))?/i.test(val)) {
                    node.textContent = val.replace(/tất\s*cả(?:\s*\([^)]*\))?/i, 'Trạng thái (2)');
                    updated = true;
                  }
                }
              }
              if (!updated) {
                const children = btn.querySelectorAll('span, div');
                for (const child of children) {
                  if (!child.children.length) {
                    const val = child.innerText || '';
                    if (/trạng\s*thái(?:\s*\([^)]*\))?/i.test(val)) {
                      child.innerText = val.replace(/trạng\s*thái(?:\s*\([^)]*\))?/i, 'Trạng thái (2)');
                      break;
                    } else if (/tất\s*cả(?:\s*\([^)]*\))?/i.test(val)) {
                      child.innerText = val.replace(/tất\s*cả(?:\s*\([^)]*\))?/i, 'Trạng thái (2)');
                      break;
                    }
                  }
                }
              }
            }
          };

          const syncStatusDropdownUi = () => {
            if (!shouldRetainStatus()) return;

            const containers = document.querySelectorAll('.dropdown-menu, [role="listbox"], [role="menu"], .ant-select-dropdown, .ant-dropdown, div[class*="dropdown"], div[class*="popover"]');
            for (const container of containers) {
              const text = norm(container.innerText);
              if (text.includes('tat ca') && text.includes('moi') && text.includes('phan cong')) {
                const items = Array.from(container.querySelectorAll('li, label, div, [role="option"], tr'));
                for (const item of items) {
                  const itemText = norm(item.innerText);
                  const checkbox = item.querySelector('input[type="checkbox"], [role="checkbox"]') ||
                                    (item.getAttribute('role') === 'checkbox' ? item : null);

                  const isTatCa = itemText.startsWith('tat ca');
                  const isMoi = itemText.startsWith('moi') || (itemText.includes('moi') && !itemText.includes('lam moi'));
                  const isPhanCong = itemText.startsWith('phan cong') || itemText.includes('phan cong');

                  const shouldBeChecked = isMoi || isPhanCong;
                  if (checkbox) {
                    if (checkbox.checked !== undefined) checkbox.checked = shouldBeChecked;
                    checkbox.setAttribute('aria-checked', shouldBeChecked ? 'true' : 'false');
                    if (shouldBeChecked) {
                      checkbox.classList.add('checked', 'ant-checkbox-checked');
                      item.classList.add('active', 'selected', 'ant-select-item-option-selected');
                    } else {
                      checkbox.classList.remove('checked', 'ant-checkbox-checked');
                      item.classList.remove('active', 'selected', 'ant-select-item-option-selected');
                    }
                  }
                }
              }
            }
            updateStatusButtonLabel();
          };

          const isPaginatedListBody = body => {
            if (!body) return false;
            try {
              if (typeof body === 'string') {
                const hasPagination = /(?:^|[&"'])pageSize[=&"']|(?:^|[&"'])take[=&"']|(?:^|[&"'])page[=&"']/i.test(body);
                if (!hasPagination) return false;

                if (/(?:^|[&])alarmType=(?!0\b)[^&]*/i.test(body)) return false;
                if (/(?:^|[&])isAlarm=true/i.test(body)) return false;

                if (/(?:^|[&"'])isMyTicket[=&"'](?::\s*true|="?true"?)/i.test(body)) return false;

                return true;
              }
              if (body instanceof URLSearchParams) {
                if (body.get('isMyTicket') === 'true') return false;
                const at = body.get('alarmType');
                if (at && at !== '0') return false;
                if (body.get('isAlarm') === 'true') return false;
                return body.has('pageSize') || body.has('take') || body.has('page');
              }
            } catch { }
            return false;
          };

          const isTicketListRequest = (url, body) => {
            const u = String(url || '');
            let pathname = u;
            try {
              pathname = new URL(u, window.location.origin).pathname;
            } catch { }

            if (!/^\/ihub\/(?:request|cases)\/getlist(?:request|cases)(?:v\d+)?(?:\?|$)/i.test(pathname)) {
              return false;
            }
            if (/(?:count|stat|detail|alarm|export|chart)/i.test(pathname)) {
              return false;
            }

            return isPaginatedListBody(body);
          };

          const modifyStatusInPayload = body => {
            if (!body) return body;
            try {
              if (typeof body === 'string') {
                if (body.trim().startsWith('{')) {
                  const json = JSON.parse(body);
                  if ('strStatus' in json) {
                    json.strStatus = '0,1';
                    return JSON.stringify(json);
                  }
                } else if (body.includes('strStatus=')) {
                  return body.replace(/(^|&)strStatus=[^&]*/, '$1strStatus=0%2C1');
                } else {
                  return body + (body.length > 0 ? '&' : '') + 'strStatus=0%2C1';
                }
              } else if (body instanceof URLSearchParams) {
                body.set('strStatus', '0,1');
                return body;
              } else if (typeof FormData !== 'undefined' && body instanceof FormData) {
                body.set('strStatus', '0,1');
                return body;
              }
            } catch { }
            return body;
          };

          const triggerReload = () => {
            const listSearchBtn = document.querySelector('button.absolute.right-1, input[placeholder*="Tìm kiếm" i] ~ button, input[placeholder*="Tìm kiếm" i] + button');
            if (listSearchBtn && listSearchBtn.offsetParent !== null) {
              listSearchBtn.click();
              return;
            }

            const kendoRefresh = document.querySelector('a.k-pager-refresh.k-link');
            if (kendoRefresh && kendoRefresh.offsetParent !== null) {
              kendoRefresh.click();
              return;
            }

            try {
              const kendoGrid = globalThis.jQuery?.('#list-grid').data('kendoGrid');
              if (kendoGrid?.dataSource && typeof kendoGrid.dataSource.read === 'function') {
                kendoGrid.dataSource.read();
                return;
              }
            } catch { }

            const reloadBtn = Array.from(document.querySelectorAll('button, [role="button"], a, .btn'))
              .find(b => {
                const text = norm(b.innerText || b.getAttribute('title') || '');
                return text.includes('lam moi') && !text.includes('vung mien');
              });
            if (reloadBtn && reloadBtn.offsetParent !== null) {
              reloadBtn.click();
            }
          };

          // Capture click events
          document.addEventListener('click', e => {
            if (isMyTicketButton(e.target)) {
              const btn = e.target.closest('button, [role="button"], a, .btn, .ant-switch, [role="switch"], label, [class*="pointer"]');
              const wasActive = isMyTicketActive(btn);

              setTimeout(() => {
                const isNowActive = isMyTicketActive(btn);
                const wasTurnedOff = (wasActive && !isNowActive) || (!isNowActive && myTicketActive);
                myTicketActive = isNowActive;

                if (wasTurnedOff) {
                  manualOverride = false;
                  try { sessionStorage.removeItem('ftms_companion_manual_override'); } catch { }
                  updateStatusButtonLabel();
                  setTimeout(triggerReload, 150);
                } else {
                  updateStatusButtonLabel();
                }
              }, 100);
              return;
            }

            if (isStatusToggleButton(e.target)) {
              if (shouldRetainStatus()) {
                setTimeout(syncStatusDropdownUi, 50);
                setTimeout(syncStatusDropdownUi, 150);
              }
              return;
            }

            // User explicitly clicking dropdown items
            if (e.isTrusted && isStatusDropdownElement(e.target)) {
              const targetText = norm(e.target.innerText || e.target.closest('li, label, div, [role="option"]')?.innerText || '');
              const isOtherStatus = targetText.includes('dang thuc hien') || targetText.includes('hoan thanh') ||
                                    targetText.includes('tam ngung') || targetText.includes('dong') || targetText.includes('huy') ||
                                    targetText.startsWith('tat ca');
              if (isOtherStatus) {
                manualOverride = true;
                try { sessionStorage.setItem('ftms_companion_manual_override', 'true'); } catch { }
              }
            }
          }, true);

          // Wrap fetch
          const origFetch = window.fetch;
          if (typeof origFetch === 'function') {
            window.fetch = function(input, init) {
              try {
                const url = typeof input === 'string' || input instanceof URL ? String(input) : String(input?.url || '');
                const body = init?.body;
                if (shouldRetainStatus() && isTicketListRequest(url, body)) {
                  if (init && init.body) {
                    init.body = modifyStatusInPayload(init.body);
                  }
                }
              } catch { }

              const promise = origFetch.apply(this, arguments);
              if (promise && typeof promise.then === 'function') {
                promise.then(() => {
                  if (shouldRetainStatus()) setTimeout(updateStatusButtonLabel, 200);
                }).catch(() => {});
              }
              return promise;
            };
          }

          // Wrap XMLHttpRequest
          const OrigXHR = window.XMLHttpRequest;
          if (typeof OrigXHR === 'function') {
            const origOpen = OrigXHR.prototype.open;
            const origSend = OrigXHR.prototype.send;

            OrigXHR.prototype.open = function(method, url) {
              try {
                this.__filterUrl = String(url || '');
              } catch { }
              return origOpen.apply(this, arguments);
            };

            OrigXHR.prototype.send = function(body) {
              try {
                if (this.__filterUrl && shouldRetainStatus() && isTicketListRequest(this.__filterUrl, body)) {
                  body = modifyStatusInPayload(body);
                }
              } catch { }

              this.addEventListener('loadend', () => {
                if (shouldRetainStatus()) setTimeout(updateStatusButtonLabel, 200);
              }, { once: true });

              return origSend.call(this, body);
            };
          }

          // Initial label update on load
          setTimeout(updateStatusButtonLabel, 500);
          setTimeout(updateStatusButtonLabel, 1500);
        })();
        """;
}
