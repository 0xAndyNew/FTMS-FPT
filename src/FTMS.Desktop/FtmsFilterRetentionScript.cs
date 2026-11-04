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
          try {
            myTicketJustToggled = sessionStorage.getItem('ftms_companion_retain_status') === 'true';
          } catch { }
          let toggleTimestamp = myTicketJustToggled ? Date.now() : 0;

          const isMyTicketButton = el => {
            if (!el) return false;
            // Ignore any elements in header or top navigation (avatar, alarm bell, search bar)
            if (el.closest('header, nav, .main-header, .navbar, #header, .header')) return false;

            const btn = el.closest('button, [role="button"], a, .btn');
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

            // In the ticket filter toolbar, the "Của tôi" button is right next to the "Vùng miền" button
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

            const icon = btn.querySelector('i, svg, [class*="user"], [class*="person"]');
            if (icon) {
              const cls = norm(icon.getAttribute('class') || '') + ' ' + norm(btn.getAttribute('class') || '');
              if (cls.includes('user') || cls.includes('person') || cls.includes('avatar') || cls.includes('profile'))
                return true;
              if (icon.tagName && icon.tagName.toLowerCase() === 'svg' && icon.querySelector('circle') && icon.querySelector('path')) {
                if (vmBtn && allBtns.indexOf(btn) > allBtns.indexOf(vmBtn) && allBtns.indexOf(btn) <= allBtns.indexOf(vmBtn) + 2)
                  return true;
              }
            }

            return false;
          };

          const isStatusDropdownElement = el => {
            if (!el) return false;
            const container = el.closest('.dropdown-menu, [role="listbox"], [role="menu"], .ant-select-dropdown, div[class*="dropdown"], div[class*="popover"]');
            if (!container) return false;
            const text = norm(container.innerText);
            return text.includes('tat ca') && text.includes('moi') && text.includes('phan cong');
          };

          const isStatusToggleButton = el => {
            if (!el) return false;
            const btn = el.closest('button, [role="button"], a, .btn');
            if (!btn) return false;
            const text = norm(btn.innerText);
            return text.includes('trang thai') && !text.includes('vung mien');
          };

          const modifyStatusInPayload = body => {
            if (!body) return body;
            try {
              if (typeof body === 'string') {
                if (body.trim().startsWith('{')) {
                  const json = JSON.parse(body);
                  if ('strStatus' in json || 'isMyTicket' in json) {
                    json.strStatus = '0,1';
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
                body.set('strStatus', '0,1');
                return body;
              } else if (typeof FormData !== 'undefined' && body instanceof FormData) {
                body.set('strStatus', '0,1');
                return body;
              }
            } catch { }
            return body;
          };

          const modifyUrl = url => {
            if (!url || typeof url !== 'string') return url;
            if (!listEndpointPattern.test(url) || /alarm/i.test(url)) return url;
            if (url.includes('strStatus=')) {
              return url.replace(/(^|[?&])strStatus=[^&]*/, '$1strStatus=0%2C1');
            }
            return url;
          };

          const updateStatusButtonLabel = () => {
            const allBtns = Array.from(document.querySelectorAll('button, [role="button"], a, .btn'));
            const statusBtn = allBtns.find(b => {
              const t = norm(b.innerText);
              return t.includes('trang thai') && !t.includes('vung mien');
            });
            if (!statusBtn) return;
            if (!statusBtn.innerText.includes('(2)')) {
              for (const node of statusBtn.childNodes) {
                if (node.nodeType === Node.TEXT_NODE && /trạng\s*thái/i.test(node.textContent || '')) {
                  node.textContent = node.textContent.replace(/trạng\s*thái(?:\s*\([^)]*\))?/i, 'Trạng thái (2)');
                }
              }
              const children = statusBtn.querySelectorAll('span, div');
              for (const child of children) {
                if (/trạng\s*thái/i.test(child.innerText || '') && !child.children.length) {
                  child.innerText = child.innerText.replace(/trạng\s*thái(?:\s*\([^)]*\))?/i, 'Trạng thái (2)');
                  break;
                }
              }
            }
          };

          const syncStatusDropdownUi = () => {
            const containers = document.querySelectorAll('.dropdown-menu, [role="listbox"], [role="menu"], .ant-select-dropdown, div[class*="dropdown"], div[class*="popover"]');
            for (const container of containers) {
              const text = norm(container.innerText);
              if (text.includes('tat ca') && text.includes('moi') && text.includes('phan cong')) {
                const items = Array.from(container.querySelectorAll('li, label, div, [role="option"], tr'));
                for (const item of items) {
                  const itemText = norm(item.innerText);
                  const checkbox = item.querySelector('input[type="checkbox"], [role="checkbox"]');
                  if (!checkbox) continue;
                  const isChecked = checkbox.checked || checkbox.getAttribute('aria-checked') === 'true' ||
                                    checkbox.classList.contains('checked') || checkbox.classList.contains('ant-checkbox-checked');
                  if (itemText === 'tat ca' && isChecked) {
                    checkbox.click();
                  } else if ((itemText === 'moi' || itemText === 'phan cong') && !isChecked) {
                    checkbox.click();
                  } else if ((itemText.includes('dang thuc hien') || itemText.includes('hoan thanh') || itemText.includes('tam ngung')) && isChecked) {
                    checkbox.click();
                  }
                }
              }
            }
            updateStatusButtonLabel();
          };

          // If previously retained via sessionStorage, apply UI sync
          if (myTicketJustToggled) {
            setTimeout(updateStatusButtonLabel, 500);
            setTimeout(updateStatusButtonLabel, 1500);
            setTimeout(updateStatusButtonLabel, 3000);
          }

          // Capture click events
          document.addEventListener('click', e => {
            if (isMyTicketButton(e.target)) {
              myTicketJustToggled = true;
              toggleTimestamp = Date.now();
              try { sessionStorage.setItem('ftms_companion_retain_status', 'true'); } catch { }
              setTimeout(updateStatusButtonLabel, 200);
              setTimeout(updateStatusButtonLabel, 600);
              setTimeout(updateStatusButtonLabel, 1200);
              setTimeout(updateStatusButtonLabel, 2500);
              return;
            }

            if (isStatusToggleButton(e.target)) {
              if (myTicketJustToggled || (Date.now() - toggleTimestamp < 15000)) {
                setTimeout(syncStatusDropdownUi, 50);
                setTimeout(syncStatusDropdownUi, 150);
              }
              return;
            }

            if (e.isTrusted && isStatusDropdownElement(e.target)) {
              // User is manually interacting with status dropdown items
              myTicketJustToggled = false;
              toggleTimestamp = 0;
              try { sessionStorage.removeItem('ftms_companion_retain_status'); } catch { }
            }
          }, true);

          // Wrap fetch
          const origFetch = window.fetch;
          if (typeof origFetch === 'function') {
            window.fetch = function(input, init) {
              try {
                let url = typeof input === 'string' || input instanceof URL ? String(input) : String(input?.url || '');
                if (!/alarm/i.test(url) && listEndpointPattern.test(url)) {
                  if (myTicketJustToggled || (Date.now() - toggleTimestamp < 6000)) {
                    if (init && init.body) {
                      init.body = modifyStatusInPayload(init.body);
                    }
                    if (url.includes('strStatus=')) {
                      const newUrl = modifyUrl(url);
                      if (typeof input === 'string') input = newUrl;
                      else if (input instanceof URL) input = new URL(newUrl);
                    }
                    setTimeout(updateStatusButtonLabel, 300);
                  }
                }
              } catch { }
              return origFetch.apply(this, arguments);
            };
          }

          // Wrap XMLHttpRequest
          const OrigXHR = window.XMLHttpRequest;
          if (typeof OrigXHR === 'function') {
            const origOpen = OrigXHR.prototype.open;
            const origSend = OrigXHR.prototype.send;

            OrigXHR.prototype.open = function(method, url) {
              try {
                if (myTicketJustToggled || (Date.now() - toggleTimestamp < 6000)) {
                  url = modifyUrl(url);
                }
                this.__filterUrl = String(url || '');
              } catch { }
              return origOpen.apply(this, arguments);
            };

            OrigXHR.prototype.send = function(body) {
              try {
                if (this.__filterUrl && !/alarm/i.test(this.__filterUrl) && listEndpointPattern.test(this.__filterUrl)) {
                  if (myTicketJustToggled || (Date.now() - toggleTimestamp < 6000)) {
                    body = modifyStatusInPayload(body);
                    setTimeout(updateStatusButtonLabel, 300);
                  }
                }
              } catch { }
              return arguments.length ? origSend.call(this, body) : origSend.apply(this, arguments);
            };
          }
        })();
        """;
}
