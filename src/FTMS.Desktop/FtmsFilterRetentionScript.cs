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

          const listEndpointPattern = /\/ihub\/(?:request|cases)\/(?:getlistrequest|getlistcases)/i;

          let myTicketJustToggled = false;
          let toggleTimestamp = 0;
          let manualOverride = false;

          const shouldRetainStatus = () => {
            if (manualOverride) return false;
            if (myTicketJustToggled || (Date.now() - toggleTimestamp < 15000)) return true;
            try {
              return sessionStorage.getItem('ftms_companion_retain_status') === 'true';
            } catch { }
            return false;
          };

          const isAlarmRequest = (url, body) => {
            const u = String(url || '');
            if (/(?:\/alarm(?:\/|$|\?)|countalarm|getlistalarm|chart)/i.test(u)) return true;
            if (/[?&]alarmType=(?!0\b)[^&]*/i.test(u)) return true;
            if (/[?&]isAlarm=true/i.test(u)) return true;

            if (body) {
              try {
                if (typeof body === 'string') {
                  if (body.trim().startsWith('{')) {
                    const j = JSON.parse(body);
                    const at = j.alarmType ?? j.input?.alarmType ?? j.filter?.alarmType;
                    if (at !== undefined && String(at) !== '0') return true;
                    if (j.isAlarm === true || j.input?.isAlarm === true || j.type === 'alarm' || j.input?.type === 'alarm') return true;
                  } else {
                    if (/(?:^|[&])alarmType=(?!0\b)[^&]*/i.test(body)) return true;
                    if (/(?:^|[&])isAlarm=true/i.test(body)) return true;
                  }
                } else if (body instanceof URLSearchParams || (typeof FormData !== 'undefined' && body instanceof FormData)) {
                  const at = body.get('alarmType');
                  if (at !== null && String(at) !== '0') return true;
                  const ia = body.get('isAlarm');
                  if (ia === 'true') return true;
                }
              } catch { }
            }
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

          const modifyStatusInPayload = body => {
            if (!body) return body;
            try {
              if (typeof body === 'string') {
                if (body.trim().startsWith('{')) {
                  const json = JSON.parse(body);
                  if ('strStatus' in json || 'isMyTicket' in json) {
                    json.strStatus = '0,1';
                    if (json.input && typeof json.input === 'object') {
                      json.input.strStatus = '0,1';
                    }
                    return JSON.stringify(json);
                  }
                } else if (body.includes('strStatus=') || body.includes('isMyTicket=')) {
                  if (body.includes('strStatus=')) {
                    return body.replace(/(^|&)strStatus=[^&]*/, '$1strStatus=0%2C1');
                  } else {
                    return body + (body.length > 0 ? '&' : '') + 'strStatus=0%2C1';
                  }
                }
              } else if (body instanceof URLSearchParams) {
                if (body.has('strStatus') || body.has('isMyTicket')) {
                  body.set('strStatus', '0,1');
                  return body;
                }
              } else if (typeof FormData !== 'undefined' && body instanceof FormData) {
                if (body.has('strStatus') || body.has('isMyTicket')) {
                  body.set('strStatus', '0,1');
                  return body;
                }
              }
            } catch { }
            return body;
          };

          const updateStatusButtonLabel = () => {
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

          // Visually reflect checkboxes when dropdown is opened WITHOUT calling .click() or triggering network requests
          const syncStatusDropdownUiVisualOnly = () => {
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

          // Capture click events
          document.addEventListener('click', e => {
            if (isMyTicketButton(e.target)) {
              manualOverride = false;
              myTicketJustToggled = true;
              toggleTimestamp = Date.now();
              try {
                sessionStorage.setItem('ftms_companion_retain_status', 'true');
              } catch { }
              setTimeout(updateStatusButtonLabel, 200);
              setTimeout(updateStatusButtonLabel, 600);
              setTimeout(updateStatusButtonLabel, 1200);
              return;
            }

            if (isStatusToggleButton(e.target)) {
              if (shouldRetainStatus()) {
                setTimeout(syncStatusDropdownUiVisualOnly, 50);
                setTimeout(syncStatusDropdownUiVisualOnly, 150);
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
                myTicketJustToggled = false;
                toggleTimestamp = 0;
                try {
                  sessionStorage.removeItem('ftms_companion_retain_status');
                } catch { }
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
                if (listEndpointPattern.test(url) && !isAlarmRequest(url, body) && shouldRetainStatus()) {
                  if (init && init.body) {
                    init.body = modifyStatusInPayload(init.body);
                  }
                }
              } catch { }
              const promise = origFetch.apply(this, arguments);
              if (promise && typeof promise.then === 'function') {
                promise.then(() => setTimeout(updateStatusButtonLabel, 200)).catch(() => {});
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
                if (this.__filterUrl && listEndpointPattern.test(this.__filterUrl) && !isAlarmRequest(this.__filterUrl, body) && shouldRetainStatus()) {
                  body = modifyStatusInPayload(body);
                  this.addEventListener('loadend', () => setTimeout(updateStatusButtonLabel, 200), { once: true });
                }
              } catch { }
              return arguments.length ? origSend.call(this, body) : origSend.apply(this, arguments);
            };
          }

          // Initial label update on load
          setTimeout(updateStatusButtonLabel, 400);
          setTimeout(updateStatusButtonLabel, 1200);
        })();
        """;
}
